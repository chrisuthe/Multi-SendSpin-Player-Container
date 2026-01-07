# Audio Pipeline Architecture

This document provides detailed documentation of the Multi-Room Audio Controller's audio processing pipeline.

## Overview

The audio pipeline transforms network-streamed audio from Music Assistant into high-quality output for USB DACs and sound cards. Key features include:

- High-quality sample rate conversion (48kHz → 192kHz)
- Synchronized multi-room playback via clock synchronization
- Dynamic playback rate adjustment for drift correction
- Support for multiple audio backends (ALSA, PulseAudio)

---

## Signal Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         Music Assistant                                  │
│                                                                          │
│  - Audio library management                                              │
│  - Streaming source selection                                            │
│  - Player group coordination                                             │
└────────────────────────────────┬────────────────────────────────────────┘
                                 │
                                 │ Sendspin Protocol
                                 │ (WebSocket + mDNS discovery)
                                 │
                                 v
┌─────────────────────────────────────────────────────────────────────────┐
│                         SendSpin.SDK                                     │
│                                                                          │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────────┐  │
│  │ ClockSync       │    │ TimedAudioBuffer│    │ SendspinClient      │  │
│  │                 │    │                 │    │                     │  │
│  │ - NTP-like sync │◄──►│ - Audio buffer  │◄───│ - Protocol handler  │  │
│  │ - Drift measure │    │ - Sync timing   │    │ - Connection mgmt   │  │
│  │ - Rate target   │    │ - Rate events   │    │ - Volume control    │  │
│  └─────────────────┘    └────────┬────────┘    └─────────────────────┘  │
│                                  │                                       │
│                                  │ TargetPlaybackRateChanged event       │
│                                  │ (0.96 - 1.04 range)                   │
└──────────────────────────────────┼──────────────────────────────────────┘
                                   │
                                   │ PCM Float32 samples (typically 48kHz)
                                   v
┌─────────────────────────────────────────────────────────────────────────┐
│                    UnifiedPolyphaseResampler                             │
│                                                                          │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │ Polyphase Filter Bank                                              │  │
│  │                                                                     │  │
│  │  Phase 0: [c0,0  c0,1  c0,2  ... c0,N]                            │  │
│  │  Phase 1: [c1,0  c1,1  c1,2  ... c1,N]                            │  │
│  │  Phase 2: [c2,0  c2,1  c2,2  ... c2,N]                            │  │
│  │     ...                                                            │  │
│  │  Phase M: [cM,0  cM,1  cM,2  ... cM,N]                            │  │
│  │                                                                     │  │
│  │  Quality Presets:                                                  │  │
│  │  - HighestQuality: 128 phases × 48 taps                           │  │
│  │  - MediumQuality:   64 phases × 32 taps [DEFAULT]                 │  │
│  │  - LowResource:     32 phases × 24 taps                           │  │
│  └───────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  Algorithm:                                                              │
│  1. Calculate effectiveRatio = (outputRate/inputRate) × playbackRate    │
│  2. For each output sample:                                              │
│     a. Calculate fractional input position                               │
│     b. Select two adjacent polyphase filters                             │
│     c. Convolve input samples with each filter                           │
│     d. Interpolate between filter outputs                                │
│     e. Advance input position by 1/effectiveRatio                        │
│                                                                          │
└────────────────────────────────┬────────────────────────────────────────┘
                                 │
                                 │ Resampled PCM Float32 (device rate)
                                 v
┌─────────────────────────────────────────────────────────────────────────┐
│                     AlsaPlayer / PulseAudioPlayer                        │
│                                                                          │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────────┐  │
│  │ Format Convert  │    │ Volume Control  │    │ Device Output       │  │
│  │                 │    │                 │    │                     │  │
│  │ Float32 → S32   │───►│ Software gain   │───►│ ALSA/PulseAudio     │  │
│  │ Float32 → S24   │    │ (0-100%)        │    │ device write        │  │
│  │ Float32 → S16   │    │                 │    │                     │  │
│  └─────────────────┘    └─────────────────┘    └─────────────────────┘  │
│                                                                          │
└────────────────────────────────┬────────────────────────────────────────┘
                                 │
                                 v
                          USB DAC / Sound Card
```

---

## Component Details

### 1. SendSpin.SDK (External Package)

The SDK handles all network communication and synchronization:

| Component | Purpose |
|-----------|---------|
| `SendspinClientService` | WebSocket connection to Music Assistant |
| `ClockSynchronization` | NTP-like clock sync between client and server |
| `TimedAudioBuffer` | Buffer with timestamp-aware sample management |

**Key Event:** `TargetPlaybackRateChanged`
- Fired when clock sync detects drift
- Rate range: 0.96 (slow down) to 1.04 (speed up)
- Used by resampler for dynamic rate adjustment

### 2. UnifiedPolyphaseResampler

The core audio processing component that handles both static rate conversion and dynamic sync adjustment.

#### Why Unified?

Previous implementation used two separate resamplers:
1. `SampleRateConverter` - High-quality windowed-sinc for static conversion
2. `ResamplingAudioSampleSource` - Linear interpolation for dynamic ±4% adjustment

**Problem:** When upsampling (48kHz → 192kHz), the linear interpolation operated on the 192kHz signal, causing aliasing that manifested as audible "warbling."

**Solution:** Single polyphase resampler that combines both operations with consistent high quality.

#### Polyphase Filter Design

The filter bank is designed using a Kaiser-windowed sinc function:

```
Prototype filter length = phases × taps
Cutoff frequency = min(1.0 / baseRatio, 1.0)
Window: Kaiser with β = 6.0

For each phase p from 0 to phases-1:
  For each tap t from 0 to taps-1:
    filterBank[p][t] = prototype[t × phases + p] × phases
```

#### Quality Presets

| Preset | Phases | Taps | Memory | CPU | Use Case |
|--------|--------|------|--------|-----|----------|
| HighestQuality | 128 | 48 | ~48 KB | High | Critical listening |
| MediumQuality | 64 | 32 | ~16 KB | Medium | General use [DEFAULT] |
| LowResource | 32 | 24 | ~6 KB | Low | Raspberry Pi, etc. |

#### Fractional Phase Interpolation

For arbitrary conversion ratios, we interpolate between adjacent polyphase filters:

```csharp
fractionalPhase = (inputPosition - floor(inputPosition)) × polyphaseCount;
phase0 = (int)fractionalPhase;
phase1 = (phase0 + 1) % polyphaseCount;
phaseFrac = fractionalPhase - phase0;

// Convolve with both filters
sum0 = convolve(input, filterBank[phase0]);
sum1 = convolve(input, filterBank[phase1]);

// Interpolate
output = sum0 + (sum1 - sum0) × phaseFrac;
```

#### Thread Safety

The playback rate is updated by the event handler thread and read by the audio callback thread. On x64/ARM64, aligned 64-bit reads/writes are atomic. The bounded range [0.96, 1.04] ensures any transient inconsistency is benign.

### 3. Audio Output (AlsaPlayer / PulseAudioPlayer)

Handles the final audio output:

| Feature | Description |
|---------|-------------|
| Format Conversion | Float32 → S32_LE/S24_LE/S16_LE based on device support |
| Volume Control | Software gain applied before output |
| Buffering | Ring buffer to handle timing variations |
| Backend Selection | ALSA for Docker, PulseAudio for HAOS |

---

## Configuration Options

### Resampler Quality

The resampler quality can be changed at runtime. The filter bank rebuilds on the next `Read()` call.

```csharp
// Default: MediumQuality
resampler.Quality = ResamplerQuality.HighestQuality;
```

### Output Sample Rate

Set via the player's output format configuration:

```csharp
var outputFormat = new AudioOutputFormat
{
    SampleRate = 192000,  // 48000, 96000, 192000
    BitDepth = 32,        // 16, 24, 32
    Channels = 2
};
```

---

## Troubleshooting

### Warbling or Pitch Wobble

**Symptom:** Audio has a warbling or pitch modulation effect

**Cause:** Historical issue with dual-resampler chain (now fixed)

**Solution:** Ensure using version 2.0.13 or later with unified resampler

### Clicks or Pops During Playback

**Symptom:** Intermittent clicks or pops in audio

**Possible Causes:**
1. Buffer underrun - increase buffer size
2. CPU overload - try LowResource quality preset
3. USB DAC issues - try different USB port

**Solution:** Check logs for underrun messages, reduce quality preset

### Sync Drift Not Correcting

**Symptom:** Player drifts out of sync over time

**Check:**
1. Verify `isClockSynced: true` in player status
2. Check logs for `Resampler: rate=` messages
3. Ensure network latency is stable

---

## Performance Considerations

### CPU Usage by Quality

| Quality | Typical CPU (per player) |
|---------|-------------------------|
| HighestQuality | ~5-8% (Raspberry Pi 4) |
| MediumQuality | ~3-5% (Raspberry Pi 4) |
| LowResource | ~2-3% (Raspberry Pi 4) |

### Memory Usage

| Component | Memory |
|-----------|--------|
| Filter Bank (MediumQuality) | ~16 KB |
| Input Buffer | ~16 KB |
| History Buffer | ~256 bytes |
| **Total per player** | **~32 KB** |

---

## Further Reading

- [ARCHITECTURE.md](ARCHITECTURE.md) - Overall system architecture
- [CODE_STRUCTURE.md](CODE_STRUCTURE.md) - Codebase organization
- SendSpin.SDK documentation (NuGet package)
