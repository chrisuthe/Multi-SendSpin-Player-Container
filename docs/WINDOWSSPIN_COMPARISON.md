# windowsSpin vs MultiRoomAudio Comparison

This document compares the timing and audio pipeline implementations between **windowsSpin** (the reference C# desktop application that achieves zero sync error) and **MultiRoomAudio** (the Docker/HAOS audio player).

---

## Executive Summary

Both applications use the same **SendSpin.SDK**, but differ significantly in their audio output model:

| Aspect | windowsSpin | MultiRoomAudio |
|--------|-------------|----------------|
| Audio Model | **Pull-based** (WASAPI pulls) | **Push-based** (we push to ALSA) |
| Thread Control | WASAPI controls timing | Our thread controls timing |
| Buffer Owner | WASAPI owns buffer, calls us | We own buffer, call ALSA |
| Latency Handling | Simple (WASAPI provides latency) | Complex (must query ALSA) |
| Platform | Windows + WASAPI | Linux + ALSA |

---

## Places to Investigate

### 1. Audio Output Model Difference (HIGH PRIORITY)

**windowsSpin (WasapiRenderer.cs)**:
```
WASAPI thread wakes up → Calls OnAudioData() → We provide samples → WASAPI outputs
```

**MultiRoomAudio (AlsaPlayer.cs:462-616)**:
```
Our thread wakes up → Read from source → Convert → Write to ALSA → ALSA blocks
```

**Investigation Points**:
- [ ] Does ALSA's blocking write introduce variable timing?
- [ ] Does our playback thread timing affect sync calculation?
- [ ] Is the 1ms sleep at line 517 introducing jitter?
- [ ] Should we switch to ALSA's mmap mode or async callbacks?

**Key Code Locations**:
- `AlsaPlayer.PlaybackLoop()` - lines 462-616
- Compare with windowsSpin's `WasapiRenderer.OnAudioData()`

---

### 2. TimedAudioBuffer Integration

Both use `TimedAudioBuffer` from the SDK, but the timing context differs.

**windowsSpin Flow**:
```
1. WASAPI pulls at precise hardware intervals
2. TimedAudioBuffer.Read() returns samples for current timestamp
3. If not yet time, returns SILENCE until timestamp arrives
4. Sync error = elapsed wall-clock - samples consumed time
```

**MultiRoomAudio Flow**:
```
1. Our thread reads continuously in a tight loop
2. BufferedAudioSampleSource reads from TimedAudioBuffer
3. SDK handles rate adjustment via TimedAudioBuffer
4. Write to PulseAudio with blocking I/O
```

**Investigation Points**:
- [ ] How does TimedAudioBuffer handle our continuous reading vs WASAPI's periodic pulls?
- [ ] Are we calling Read() too frequently, affecting sync error calculation?
- [ ] Check `BufferedAudioSampleSource` - does it properly respect timestamps?
- [ ] Verify `TimedAudioBuffer.TargetBufferMilliseconds` setting (250ms vs default)

**Key Code Locations**:
- `PlayerManagerService.CreatePlayerAsync()` - line 449-461 (source factory)
- `TimedAudioBuffer` in SDK - check how it calculates sync error

---

### 3. Playback Start Behavior

**windowsSpin**: Uses "scheduled start" - outputs silence until the first audio timestamp arrives, then begins precisely.

**MultiRoomAudio**: Waits for convergence (`waitForConvergence: true`), then starts playing.

**Investigation Points**:
- [ ] Does our convergence wait (lines 463-464) match windowsSpin's scheduled start?
- [ ] Are we starting playback at the right moment?
- [ ] Does `HasMinimalSync` (2 measurements) give enough accuracy?
- [ ] What's the state of the buffer when we start playing?

**Key Code Locations**:
- `AudioPipeline` constructor params - `waitForConvergence`, `convergenceTimeoutMs`
- SDK's `TimedAudioBuffer.ScheduledStartTime` property

---

### 4. Output Latency Reporting

**windowsSpin**: WASAPI directly reports latency via `AudioClient.StreamLatency`.

**MultiRoomAudio**: Queries ALSA buffer size via `snd_pcm_get_params()`.

**Investigation Points**:
- [ ] Verify our reported latency matches actual output delay
- [ ] Check if ALSA plugins (dmix, PulseAudio) add unmeasured latency
- [ ] Consider using `snd_pcm_delay()` for real-time delay instead of static buffer size
- [ ] Compare: What if we set OutputLatencyMs to 0? Does sync error stabilize?

**Key Code Locations**:
- `AlsaPlayer.InitializeAsync()` - lines 214-229 (latency query)
- `AlsaNative.GetParams()` and `AlsaNative.CalculateLatencyMs()`

**Important SDK Note**:
From windowsSpin analysis - **OutputLatencyMs is NOT used in sync error calculation!** It's only for diagnostics. The sync error is calculated as:
```
SyncError = ElapsedWallClockTime - (SamplesRead / SampleRate)
```

---

### 5. Clock Synchronization Behavior

Both use `KalmanClockSynchronizer` from SDK.

**windowsSpin**: Reports stable offset (e.g., -1.7 trillion ms) with ~0.14ms uncertainty.

**MultiRoomAudio**: Should report similar stability but observe the sync ERROR is different.

**Investigation Points**:
- [ ] Clock offset should stabilize quickly - is ours stable?
- [ ] Check `clockStatus.IsConverged` and `clockStatus.MeasurementCount`
- [ ] Drift rate (ppm) - is it reasonable (typically <50 ppm)?
- [ ] Does network latency affect convergence on different systems?

**Key Code Locations**:
- `PlayerManagerService.GetPlayerStats()` - lines 1211-1219 (clock stats)
- Stats for Nerds API - `/api/players/{name}/stats`

---

### 6. Sync Correction Options

Both use `SyncCorrectionOptions`, but with different tuning:

**MultiRoomAudio** uses `ReadRaw()` and handles sync correction externally in `BufferedAudioSampleSource`:
- 5ms deadband (no correction for small errors)
- Frame drop/insert with 3-point weighted interpolation
- Correction rate scales with error magnitude (10-500 frames between corrections)

**windowsSpin**: Uses SDK defaults or similar settings.

**Investigation Points**:
- [ ] Are our wider deadbands masking the real problem?
- [ ] Try using SDK defaults to see if behavior changes
- [ ] Check `StartupGracePeriodMicroseconds` - 500ms enough?
- [ ] What happens if we increase `MaxSpeedCorrection` to 0.1?

**Key Code Locations**:
- `PlayerManagerService.PulseAudioSyncOptions` - lines 59-77

---

### 7. Buffer Configuration

**MultiRoomAudio**:
```csharp
AudioBufferCapacityMs = 8000      // 8 seconds capacity
TargetBufferMs = 250              // 250ms target
```

**windowsSpin**: Similar values, but behavior may differ.

**Investigation Points**:
- [ ] Is 250ms target too low? Sendspin spec allows up to 5 seconds
- [ ] Check if buffer is filling beyond target (we've seen 4800ms)
- [ ] What's the relationship between target and actual during stable playback?

---

### 8. Sample Rate Conversion Path

**MultiRoomAudio**: Direct passthrough to PulseAudio. PulseAudio handles all sample rate conversion.

**windowsSpin**: Uses WASAPI, which may use OS-level conversion.

**Investigation Points**:
- [ ] Does PulseAudio's resampling introduce timing artifacts?
- [ ] Check PulseAudio resampler quality settings
- [ ] Verify timing behavior with different PulseAudio configurations

**Key Code Locations**:
- `PulseAudioPlayer.cs`
- `BufferedAudioSampleSource.cs`

---

## Quick Tests to Run

1. **Match Rates Test**: Set output rate = input rate to minimize PulseAudio conversion
2. **Latency Zero Test**: Hardcode `OutputLatencyMs = 0` and observe sync error
3. **SDK Defaults Test**: Use default `SyncCorrectionOptions` instead of custom
4. **Buffer Target Test**: Try `TargetBufferMs = 500` or `1000`
5. **Thread Priority Test**: Run with `ThreadPriority.Highest` or real-time scheduling

---

## Key Files to Compare

| Component | windowsSpin | MultiRoomAudio |
|-----------|-------------|----------------|
| Audio Output | `WasapiRenderer.cs` | `PulseAudioPlayer.cs` |
| Stats/Debug | `StatsViewModel.cs` | `PlayerManagerService.GetPlayerStats()` |
| Main Entry | `MainViewModel.cs` | `PlayerManagerService.CreatePlayerAsync()` |
| Pipeline | SDK's `AudioPipeline` | Same SDK (direct passthrough) |

---

## Hypothesis

The ~200ms sync error likely comes from one of these causes:

1. **Push vs Pull Timing**: Our push-based model may have different timing characteristics than WASAPI's pull model, causing the SDK's sync error calculation to be offset.

2. **Timestamp Interpretation**: The moment we "consume" samples from the buffer may not align with when they actually play through ALSA, creating a systematic offset.

3. **Initial Buffer State**: How we start playback and the buffer state at that moment may differ from windowsSpin's scheduled start behavior.

4. **Latency in the Wrong Place**: Even though OutputLatencyMs "isn't used" in sync calculation, there may be implicit expectations in the SDK about when samples are consumed vs played.

---

## Next Steps

1. Add detailed logging at each step to compare timing with windowsSpin
2. Create a test mode that dumps timestamps for analysis
3. Compare the exact sequence of SDK calls between both apps
4. Consider implementing ALSA callback mode for true pull-based operation
