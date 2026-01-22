# Audio Pipeline Architecture

This document provides detailed documentation of the Multi-Room Audio Controller's audio processing pipeline.

## Overview

The audio pipeline streams audio from Music Assistant to USB DACs and sound cards via PulseAudio. The architecture uses direct passthrough with PulseAudio handling all format conversion natively.

Key features:
- Direct audio passthrough to PulseAudio
- Synchronized multi-room playback via clock synchronization
- Dynamic playback rate adjustment for drift correction via SDK
- PulseAudio handles sample rate and format conversion to devices

---

## Signal Flow Diagram

```
+-------------------------------------------------------------------------+
|                         Music Assistant                                  |
|                                                                          |
|  - Audio library management                                              |
|  - Streaming source selection                                            |
|  - Player group coordination                                             |
+------------------------------------+------------------------------------+
                                     |
                                     | Sendspin Protocol
                                     | (WebSocket + mDNS discovery)
                                     |
                                     v
+-------------------------------------------------------------------------+
|                         SendSpin.SDK                                     |
|                                                                          |
|  +-------------------+    +-------------------+    +---------------------+
|  | ClockSync         |    | TimedAudioBuffer  |    | SendspinClient      |
|  |                   |    |                   |    |                     |
|  | - NTP-like sync   |<-->| - Audio buffer    |<---| - Protocol handler  |
|  | - Drift measure   |    | - Sync timing     |    | - Connection mgmt   |
|  | - Rate target     |    | - Rate correction |    | - Volume control    |
|  +-------------------+    +---------+---------+    +---------------------+
|                                     |                                     |
|                                     | Sync-adjusted PCM Float32 samples   |
+-------------------------------------+-------------------------------------+
                                      |
                                      v
+-------------------------------------------------------------------------+
|                    BufferedAudioSampleSource                             |
|                                                                          |
|  - Bridges TimedAudioBuffer to IAudioSampleSource                        |
|  - Reads raw samples, applies external sync correction                   |
|  - Frame drop/insert with 3-point weighted interpolation                 |
+-------------------------------------+------------------------------------+
                                      |
                                      | PCM Float32 samples (source rate)
                                      v
+-------------------------------------------------------------------------+
|                         PulseAudioPlayer                                 |
|                                                                          |
|  +-------------------+    +-------------------+    +---------------------+
|  | Format Convert    |    | Volume Control    |    | Device Output       |
|  |                   |    |                   |    |                     |
|  | Float32 -> S32    |--->| Software gain     |--->| PulseAudio          |
|  | Float32 -> S24    |    | (0-100%)          |    | pa_simple API       |
|  | Float32 -> S16    |    |                   |    |                     |
|  +-------------------+    +-------------------+    +---------------------+
|                                                                          |
+-------------------------------------+------------------------------------+
                                      |
                                      v
+-------------------------------------------------------------------------+
|                         PulseAudio Server                                |
|                                                                          |
|  - Handles sample rate conversion to device native rate                  |
|  - Manages audio routing and mixing                                      |
|  - Device format negotiation                                             |
+-------------------------------------+------------------------------------+
                                      |
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
| `TimedAudioBuffer` | Buffer with timestamp-aware sample management and sync correction |

**Sync Correction:** Handled externally by `BufferedAudioSampleSource` using frame drop/insert with interpolation. The SDK provides sync error measurement via `SmoothedSyncErrorMicroseconds`.

### 2. BufferedAudioSampleSource

Bridges the SDK's `ITimedAudioBuffer` to the audio player's `IAudioSampleSource` interface, handling external sync correction with interpolation.

```csharp
public sealed class BufferedAudioSampleSource : IAudioSampleSource
{
    // Reads raw samples from SDK buffer (ReadRaw - no SDK correction)
    // Applies player-controlled sync correction via frame drop/insert
    // Uses 3-point weighted interpolation to minimize audible artifacts
}
```

**Sync Correction Algorithm:**

| Operation | Condition | Algorithm |
|-----------|-----------|-----------|
| Frame DROP | 3+ frames available | `0.25*A + 0.5*B + 0.25*C` (Gaussian kernel) |
| Frame DROP | 2 frames available | `(A + B) / 2` (linear fallback) |
| Frame INSERT | 2+ frames available | `(current + next) / 2` (true lookahead) |
| Frame INSERT | 1 frame available | `(lastOutput + current) / 2` (fallback) |

The 3-point weighted interpolation considers the frame after the drop point for smoother blends. Corrections are rate-limited based on sync error magnitude (10-500 frames between corrections) and only applied outside a 5ms deadband.

### 3. PulseAudioPlayer

Handles the final audio output using PulseAudio's simple API:

| Feature | Description |
|---------|-------------|
| Format Conversion | Float32 -> S32_LE/S24_LE/S16_LE based on configured bit depth |
| Volume Control | Software gain applied before output |
| Reconnection | Automatic reconnection with exponential backoff |
| Backend Selection | PulseAudio for both HAOS and Docker environments |

### 4. PulseAudio Server

PulseAudio handles all format conversion to the target device:
- **Sample Rate Conversion**: Converts to device native rate (e.g., 48kHz -> 192kHz)
- **Format Negotiation**: Adapts to device capabilities automatically
- **Mixing**: Allows multiple audio streams if needed

---

## Configuration Options

### Output Format

Set via the player's output format configuration:

```csharp
var outputFormat = new AudioOutputFormat
{
    SampleRate = 192000,  // 48000, 96000, 192000 (sent to PulseAudio)
    BitDepth = 32,        // 16, 24, 32
    Channels = 2
};
```

PulseAudio will convert to this format and further convert to the device's native format if needed.

---

## Troubleshooting

### Clicks or Pops During Playback

**Symptom:** Intermittent clicks or pops in audio

**Possible Causes:**
1. Buffer underrun - increase buffer size
2. PulseAudio configuration issues
3. USB DAC issues - try different USB port

**Solution:** Check logs for underrun messages, verify PulseAudio is running correctly

### Sync Drift Not Correcting

**Symptom:** Player drifts out of sync over time

**Check:**
1. Verify `isClockSynced: true` in player status
2. Check logs for sync correction messages
3. Ensure network latency is stable

### Constant Sync Error with Growing Buffer

**Symptom:** Stats for Nerds shows:
- Constant sync error (e.g., -199ms or +250ms)
- Buffer growing well beyond target (e.g., 4000ms vs 250ms target)
- Playback rate stuck at minimum or maximum (0.96x or 1.04x)

**Cause:** Output latency mismatch - the actual device latency differs from reported latency

**Solution:**
- Check PulseAudio latency via `pa_simple_get_latency()`
- Verify device is correctly configured in PulseAudio

**Diagnostic:** Open Stats for Nerds and check "Output Latency" in Clock Sync section.

---

## Performance Considerations

### CPU Usage

With PulseAudio handling resampling, CPU usage is minimal:

| Component | Typical CPU (per player) |
|-----------|-------------------------|
| SDK Processing | ~1-2% |
| PulseAudio | Handled by system |

### Memory Usage

| Component | Memory |
|-----------|--------|
| Audio Buffer | ~32 KB |
| PulseAudio Buffers | ~8 KB |
| **Total per player** | **~40 KB** |

---

## Further Reading

- [ARCHITECTURE.md](ARCHITECTURE.md) - Overall system architecture
- [CODE_STRUCTURE.md](CODE_STRUCTURE.md) - Codebase organization
- SendSpin.SDK documentation (NuGet package)
