# Main vs Dev Branch Comparison

## Summary

| Aspect | Main Branch | Dev Branch |
|--------|-------------|------------|
| Audio Backend | PulseAudio | ALSA (direct) |
| Sync Error | None (~0ms) | ~200ms constant |
| Playback Timing | Wrong times | Correct times |
| Resampler | `ResamplingAudioSampleSource` | `UnifiedPolyphaseResampler` |
| Rate Conversion | None (48kHz → 48kHz) | Configurable (48kHz → 192kHz) |
| Output Format | Float32 | S16/S24/S32 (configurable) |

---

## Key Findings

### 1. The Sync Error is NOT Caused by the Resampler

Testing with Native Rate mode (48kHz → 48kHz, no rate conversion) still shows ~200ms sync error. This proves the `UnifiedPolyphaseResampler` is not the cause.

### 2. The Difference is PulseAudio vs ALSA

**Main branch** uses PulseAudio:
- PulseAudio handles its own timing internally
- Has ~15ms timing jitter (which we compensate for with wider deadbands)
- Uses `pa_simple_write()` - blocking write to PulseAudio server
- PulseAudio manages the buffer and timing

**Dev branch** uses ALSA directly:
- ALSA provides raw hardware access
- We get actual buffer sizes via `snd_pcm_get_params()`
- Uses `snd_pcm_writei()` - blocking write to hardware
- We are responsible for timing

### 3. Both Use Push-Based Model

Both branches push audio to the output:
```
Source.Read() → Resampler → Player.Write()
```

Neither is pull-based like windowsSpin's WASAPI implementation.

---

## Detailed Comparison

### Audio Player

#### Main: PulseAudioPlayer
```csharp
// Write to PulseAudio
SimpleWrite(_paHandle, (IntPtr)ptr, (UIntPtr)(samplesRead * sizeof(float)), out var error);
```
- Uses `pa_simple_*` API
- Buffer: 50ms target (BufferMs = 50)
- Latency query: `pa_simple_get_latency()`
- Format: Float32 only

#### Dev: AlsaPlayer
```csharp
// Write to ALSA
AlsaNative.WriteInterleaved(pcmHandle, (IntPtr)ptr, (nuint)frames);
```
- Uses `snd_pcm_*` API
- Latency: 50ms target (TargetLatencyUs = 50000)
- Actual latency: Queried via `snd_pcm_get_params()`
- Format: Configurable (S16/S24/S32)

### Source Factory (Pipeline Creation)

#### Main Branch
```csharp
sourceFactory: (buffer, timeFunc) =>
{
    var source = new BufferedAudioSampleSource(buffer, timeFunc);
    // Wrap with resampling for smooth playback rate adjustment (±4%)
    return new ResamplingAudioSampleSource(
        source,
        buffer,
        _loggerFactory.CreateLogger<ResamplingAudioSampleSource>());
}
```

#### Dev Branch
```csharp
sourceFactory: (buffer, timeFunc) =>
{
    IAudioSampleSource source = new BufferedAudioSampleSource(buffer, timeFunc);
    var targetRate = outputFormat?.SampleRate ?? buffer.Format.SampleRate;

    // Unified polyphase resampler handles both rate conversion and sync adjustment
    return new UnifiedPolyphaseResampler(
        source,
        buffer.Format.SampleRate,
        targetRate,
        buffer,
        _loggerFactory.CreateLogger<UnifiedPolyphaseResampler>());
}
```

### Resampler Comparison

| Aspect | Main: ResamplingAudioSampleSource | Dev: UnifiedPolyphaseResampler |
|--------|-----------------------------------|--------------------------------|
| Algorithm | Linear interpolation | Polyphase FIR (Kaiser sinc) |
| Rate Conversion | None | Yes (any ratio) |
| Sync Adjustment | ±4% playback rate | ±4% playback rate |
| Quality | Basic (OK for ±4%) | High (needed for 4x upsampling) |
| Buffer | 8192 frames | Variable (based on quality) |

---

## Hypothesis: Why Main Works and Dev Doesn't

### Theory 1: PulseAudio's Internal Timing

PulseAudio has its own timing correction. When we write to PulseAudio:
1. PulseAudio buffers the audio
2. PulseAudio may adjust timing internally
3. Our sync error calculation doesn't account for PulseAudio's internal buffer

In contrast, ALSA gives us raw hardware access:
1. We write directly to the hardware buffer
2. ALSA reports actual buffer sizes
3. Our OutputLatencyMs calculation is based on actual hardware

### Theory 2: Latency Reporting

**Main (PulseAudio):**
```csharp
var latencyUs = SimpleGetLatency(_paHandle, out var latencyError);
OutputLatencyMs = (int)(latencyUs / 1000);
```

**Dev (ALSA):**
```csharp
var getResult = AlsaNative.GetParams(_pcmHandle, out var actualBufferSize, out var actualPeriodSize);
OutputLatencyMs = AlsaNative.CalculateLatencyMs(actualBufferSize, (uint)actualSampleRate);
```

The ALSA latency might be reported differently than PulseAudio's latency.

### Theory 3: Buffer Management Difference

PulseAudio's `pa_simple_write()` vs ALSA's `snd_pcm_writei()` may have different blocking behavior:

- **PulseAudio**: Blocks until the server accepts the data (into PA's buffer)
- **ALSA**: Blocks until the hardware buffer has room

This could cause a timing offset between when we think audio plays vs when it actually plays.

---

## Critical Observation: "Wrong Times" in Main

You mentioned main "has other issues of playing audio at the wrong times". This is interesting because:

1. Main has no sync error (error ≈ 0ms)
2. But audio plays at wrong times
3. Dev has sync error (~200ms)
4. But... does audio play at the RIGHT times in dev?

**Question**: Is the ~200ms sync error in dev actually causing audio to play LATE by 200ms? Or is the sync error measurement wrong?

---

## Recommended Investigation

### 1. Add OutputLatencyMs to Sync Calculation

The SDK's sync calculation might not be accounting for our OutputLatencyMs properly. Check if:

```csharp
// Sync error should include output latency offset
effectiveSyncError = measuredSyncError - outputLatencyMs
```

### 2. Compare Clock Sync Setup

Check if the `IClockSynchronizer.OutputLatencyMs` property is being set correctly in both branches.

### 3. Test Same Backend

Try using PulseAudio in dev branch (just change BackendFactory) to see if the sync error disappears. This would confirm it's a backend timing issue.

### 4. Log Actual Play Time

Add logging to measure when audio actually reaches the speakers (using external measurement) vs when we think it does.

---

## Files Changed Between Branches

### Removed from Main
- `src/MultiRoomAudio/Audio/PulseAudio/PulseAudioPlayer.cs` (still exists but not used)

### Added in Dev
- `src/MultiRoomAudio/Audio/Alsa/AlsaPlayer.cs`
- `src/MultiRoomAudio/Audio/Alsa/AlsaNative.cs`
- `src/MultiRoomAudio/Audio/Alsa/AlsaBackend.cs`
- `src/MultiRoomAudio/Audio/Alsa/AlsaDeviceEnumerator.cs`
- `src/MultiRoomAudio/Audio/Alsa/AlsaCapabilityProbe.cs`
- `src/MultiRoomAudio/Audio/UnifiedPolyphaseResampler.cs`
- `src/MultiRoomAudio/Audio/BitDepthConverter.cs`
- `src/MultiRoomAudio/Audio/BackendFactory.cs`
- `src/MultiRoomAudio/Audio/IBackend.cs`

### Modified in Dev
- `src/MultiRoomAudio/Services/PlayerManagerService.cs` - Backend abstraction, output format config
- `src/MultiRoomAudio/Services/ConfigurationService.cs` - Added NativeRate, OutputSampleRate, OutputBitDepth
- `src/MultiRoomAudio/Models/PlayerConfig.cs` - Added NativeRate, OutputFormat

---

## A/B Test Option Added

An A/B test option has been added to compare the two resamplers:

1. In the "Add Player" dialog, check **"Native Rate (no upsampling)"**
2. This enables the **"Use Simple Resampler (A/B test)"** checkbox
3. Check it to use the main branch's linear resampler instead of the polyphase resampler

**Test Procedure:**
1. Create a player with Native Rate ON, Simple Resampler OFF → expect ~200ms sync error
2. Create a player with Native Rate ON, Simple Resampler ON → if sync error disappears, confirms it's the polyphase resampler

---

## Next Steps

1. **A/B test**: Use the new checkbox to compare resamplers (see above)
2. **Deep dive**: Compare how OutputLatencyMs is used in sync calculation
3. **External validation**: Use external tool to measure actual audio latency vs calculated
