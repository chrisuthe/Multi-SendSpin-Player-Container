# Audio Synchronization Architecture

This document explains how the Sendspin SDK achieves sample-accurate multi-room audio synchronization.

## Overview

Multi-room audio synchronization requires solving several timing challenges:
1. Server and client clocks are different and drift over time
2. Network latency varies and is unpredictable
3. Audio hardware has its own clock that may differ from the system clock
4. Virtual machines can have erratic system timers

The SDK addresses these through a layered correction pipeline.

## The Correction Pipeline

```
Network Audio Chunks (server timestamps)
         │
         ▼
┌─────────────────────────────────────┐
│   Layer 1: Clock Synchronization    │  [ClockSync]
│   KalmanClockSynchronizer           │
│   - NTP 4-timestamp method          │
│   - Offset + Drift estimation       │
└─────────────────────────────────────┘
         │ (converts server→local time)
         ▼
┌─────────────────────────────────────┐
│   Layer 2: Timing Sources           │  [Timing]
│   Audio Clock → MonotonicTimer →    │
│   Wall Clock (priority order)       │
│   - VM timer jitter filtering       │
└─────────────────────────────────────┘
         │ (provides stable local time)
         ▼
┌─────────────────────────────────────┐
│   Layer 3: Sync Error Tracking      │  [SyncError]
│   TimedAudioBuffer                  │
│   - syncError = elapsed - samplesRead│
│   - EMA smoothing (α=0.1)           │
└─────────────────────────────────────┘
         │ (measures: are we on time?)
         ▼
┌─────────────────────────────────────┐
│   Layer 4: Correction Strategies    │  [Correction]
│   - Deadband (<1ms): no action      │
│   - Resampling (1-15ms): rate adj   │
│   - Drop/Insert (>15ms): frames     │
│   - Re-anchor (>500ms): restart     │
└─────────────────────────────────────┘
         │
         ▼
      Audio Output
```

## Correctable Factors (Pipeline Order)

| # | Factor | When | What It Corrects | Log Category |
|---|--------|------|------------------|--------------|
| 1 | Network RTT | Clock sync | Measurement uncertainty | `[ClockSync]` |
| 2 | Clock Offset | Timestamp conversion | Server↔Client difference | `[ClockSync]` |
| 3 | Clock Drift | Timestamp conversion | Diverging clocks | `[ClockSync]` |
| 4 | Static Delay | Clock offset | User multi-room alignment | `[ClockSync]` |
| 5 | Startup Latency | Playback start | Backend pre-fill | `[Playback]` |
| 6 | VM Timer Jitter | Every time read | Forward/backward jumps | `[Timing]` |
| 7 | Timing Source | Every buffer read | audio-clock/monotonic/wall | `[Timing]` |
| 8 | Sync Error | Every buffer read | Elapsed vs samples-read | `[SyncError]` |
| 9 | EMA Smoothing | Every sync update | Measurement jitter | `[SyncError]` |
| 10 | Resampling | Sample output | Gradual speed adjust (1-15ms) | `[Correction]` |
| 11 | Frame Drop | Sample output | Catch up (>15ms behind) | `[Correction]` |
| 12 | Frame Insert | Sample output | Slow down (>15ms ahead) | `[Correction]` |
| 13 | Re-anchor | Catastrophic | Clear buffer (>500ms) | `[Correction]` |

## Layer 1: Clock Synchronization

### KalmanClockSynchronizer

Synchronizes the client's local clock with the server's clock using the NTP 4-timestamp method.

**Algorithm:**
1. Client sends `client/time` with T1 (local transmit time)
2. Server responds with `server/time` containing T2 (server receive), T3 (server transmit)
3. Client records T4 (local receive time)
4. Offset = ((T2 - T1) + (T3 - T4)) / 2

**Two state variables tracked:**
- **Offset**: Server clock is ahead by this many microseconds
- **Drift**: Rate at which offset changes (μs/s)

**Convergence levels:**
- `HasMinimalSync` (2+ measurements): Quick start (~300ms)
- `IsConverged` (5+ measurements): Full convergence
- `IsDriftReliable` (drift uncertainty <50μs/s): Safe to extrapolate

### Static Delay

User-configurable offset for multi-room alignment (e.g., compensate for different speaker distances).
- Positive values = delay playback (play later)
- Negative values = advance playback (play earlier)

## Layer 2: Timing Sources

### Priority Order

1. **Audio Hardware Clock** (VM-immune, most accurate)
   - PulseAudio: `pa_stream_get_time()`
   - Uses the sound card's crystal oscillator
   - Immune to hypervisor scheduling issues

2. **MonotonicTimer** (filtered wall clock)
   - Wraps system Stopwatch with jump filtering
   - Clamps forward jumps to 50ms max
   - Absorbs backward jumps (time never goes backward)

3. **Wall Clock** (fallback)
   - Raw `HighPrecisionTimer` using `Stopwatch.GetTimestamp()`
   - ~100ns precision on Windows via QueryPerformanceCounter

### VM Timer Jitter

In virtual machines, the system timer can exhibit erratic behavior:
- **Forward jumps**: Timer suddenly advances by hundreds of ms
- **Backward jumps**: Timer returns lower values than previous calls

The `MonotonicTimer` filters these anomalies while preserving real drift detection.

## Layer 3: Sync Error Tracking

### TimedAudioBuffer

Calculates how far ahead or behind playback is:

```
syncError = elapsedTimeMicroseconds - samplesReadTimeMicroseconds
```

- **Positive** = behind schedule (need to catch up)
- **Negative** = ahead of schedule (need to slow down)

### EMA Smoothing

Exponential Moving Average with α=0.1 filters measurement jitter:
- Reaches 63% of a step change in ~10 updates
- Prevents oscillation from noisy measurements
- Only smoothed error is used for correction decisions

## Layer 4: Correction Strategies

### Tiered Correction

| Error Range | Strategy | Description |
|-------------|----------|-------------|
| **< 1ms** | Deadband | No correction needed |
| **1-15ms** | Resampling | Gradual playback rate adjustment (±2% max) |
| **> 15ms** | Drop/Insert | Frame manipulation for faster convergence |
| **> 500ms** | Re-anchor | Clear buffer and restart sync |

### Resampling (Tier 2)

Imperceptible speed adjustment using proportional control:
```
rate = 1.0 + (syncError / targetSeconds / 1,000,000)
rate = clamp(rate, 0.98, 1.02)  // ±2% max
```

### Frame Drop/Insert (Tier 3)

When sync error exceeds 15ms:
- **Dropping**: Skip a frame every N reads (catches up)
- **Inserting**: Repeat a frame every N reads (slows down)

Uses 3-point weighted interpolation to minimize audible artifacts.

## Troubleshooting Guide

| Symptom | Likely Cause | Logs to Check |
|---------|--------------|---------------|
| Audio pops/clicks | Frame drop/insert | `[Correction] Started/Ended` |
| Gradual drift | Clock drift unreliable | `[ClockSync] Drift reliable` |
| Sudden time jump | VM timer jitter | `[Timing] Source changed` |
| Massive corrections at start | Clock offset calc issue | `[Playback] Starting playback` offset value |
| No audio | Buffer underrun | `[Buffer] Underrun` |
| Choppy audio | Buffer overrun | `[Buffer] Overrun` |

### Log Category Quick Reference

- **[ClockSync]** - Clock sync convergence, drift reliability, static delay
- **[Timing]** - Which timing source is active, source transitions
- **[Playback]** - Pipeline start/stop, startup latency configuration
- **[SyncError]** - Periodic sync status (every 5s during playback)
- **[Correction]** - When frame drop/insert starts and ends
- **[Buffer]** - Overrun/underrun events

### Useful Log Patterns

**Healthy playback:**
```
[ClockSync] Converged after 5 measurements
[Timing] Using audio hardware clock for sync timing (VM-immune)
[Playback] Starting playback: buffer=500ms, sync offset=+12.3ms
[SyncError] OK: error=+0.5ms, drift=-0.3μs/s, buffer=4800ms
```

**VM timer issues:**
```
[Timing] Source changed: audio-clock → monotonic
[SyncError] Drift: error=+52ms, ...timing=[monotonic: Forward: 3 (0.01%, max 150ms)]
[Correction] Started: DROPPING (syncError=+52ms, timing=monotonic)
```

**Network/decoding issues:**
```
[Buffer] Underrun: 5 events in last 1000ms (total: 12)
[Correction] Started: INSERTING (syncError=-35ms, ...)
```
