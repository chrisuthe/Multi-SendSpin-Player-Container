# Timer / Player Sync Testing — Design

**Date:** 2026-06-24
**Branch:** `feat/issue-233-sdk-update`
**Status:** Approved design; implementation pending.

## Motivation

The SDK 9.1.0 upgrade (issue #233) and the follow-on sync/stability work changed
behavior that is easy to regress silently and impossible to eyeball:

- The delay-offset **sign inversion** (`StaticDelayMs = -delayMs`) that preserves our
  "positive = play later" convention across the SDK v8.0.0 `static_delay_ms` sign flip.
- The **audio-clock gate** (`USE_AUDIO_CLOCK`, default off) that keeps sync on the SDK's
  VM-resilient `MonotonicTimer` instead of the DAC clock, avoiding output-prefill drift.
- The **re-anchor on offset change** (`Pipeline.ReanchorTiming()`) that replaced a full
  player restart, so offset tuning no longer stalls for the server transmit-ahead window.

The repository currently has **no test project**. This design stands up the first one,
mirroring the proven approach in the sibling `windowsSpin` client: drive the real SDK
primitives (`TimedAudioBuffer` + a fake `IClockSynchronizer`) to assert sync **invariants**,
and exercise our own glue code through small testable seams.

## Goals

- Pin the three behaviors above against regression.
- Test **our** code paths (sign conversion, sync-options config, gate default), not just
  re-test the SDK.
- Establish a reusable xUnit test project the repo can grow.

## Non-goals

- Testing native PulseAudio playback (P/Invoke into `libpulse`) — not runnable cross-platform.
- Multi-process testing of the static env-var read.
- The deferred sync-correction tuning change (4% → 2%); out of scope here.

## Production seams (minimal)

Three small, self-justifying extractions plus one attribute so tests can exercise our logic:

1. **`PlayerManagerService.UserDelayToStaticDelayMs(int delayMs) => -delayMs`**
   `internal static` pure method. Both existing write sites
   (`InitializeAndConnectPlayer`, `SetDelayOffset`) call it instead of inlining `-delayMs`.

2. **`PulseAudioPlayer.ParseUseAudioClock(string? envValue)`**
   `internal static` pure method holding the truthy-parse. The static field becomes
   `UseAudioClock = ParseUseAudioClock(Environment.GetEnvironmentVariable("USE_AUDIO_CLOCK"))`.

3. **`PulseAudioSyncOptions`** — change `private static readonly` → `internal static readonly`
   so tests use the real configuration.

4. **`[assembly: InternalsVisibleTo("MultiRoomAudio.Tests")]`** on the main project.

## Test project

- Path: `tests/MultiRoomAudio.Tests/MultiRoomAudio.Tests.csproj` (the `.slnx` already
  reserves an empty `/tests/` folder).
- Framework: **net8.0** (matches the app).
- Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.
- `<ProjectReference>` to `MultiRoomAudio.csproj`. Native P/Invoke never loads because no
  test calls those methods.

## Test files

### `DelayOffsetConventionTests` — sign inversion
- `UserDelayToStaticDelayMs_Negates`: pure — `200 → -200`, `-200 → 200`, `0 → 0`.
- `PositiveOffset_SchedulesLater`: drive the **real** `KalmanClockSynchronizer`;
  `ServerToClientTime(T)` with `StaticDelayMs = UserDelayToStaticDelayMs(200)` is **+200ms
  later** than at `StaticDelayMs = 0`. The delta isolates the offset (robust to clock
  convergence) and catches a future SDK sign-convention flip.
- `NegativeOffset_SchedulesEarlier`: the mirror case.

### `StaticDelayReanchorTests` — re-anchor assumption (port of windowsSpin)
- Pre-fill a `TimedAudioBuffer` (with our `PulseAudioSyncOptions`), read a few chunks to
  establish playback, then `ResetSyncTracking()` (what `ReanchorTiming()` forwards to):
  assert `BufferedMilliseconds` is **preserved**.
- Contrast: `Clear()` **empties** the buffer. This is the exact regression that would
  reintroduce the "tens of seconds of silence" on offset tuning.

### `SyncAlignmentTests` — sync invariant at the buffer level
- Zero-drift session through `TimedAudioBuffer` (with our `PulseAudioSyncOptions`) → sync error
  stays **~0** with **zero** net correction and `TargetPlaybackRate == 1.0` (the corrector does
  not hunt on a steady stream).
- Inject a 1% playback-clock drift → the buffer **detects** it: `TargetPlaybackRate` moves off
  1.0 (to our `MaxSpeedCorrection` cap) and a clear sync error registers.

> **Implementation finding (2026-06-24):** the original plan was to demonstrate the
> *uncompensated-prefill drift* (the device-clock failure mode that motivates `USE_AUDIO_CLOCK`
> defaulting off). Measurement showed the buffer's internal `Read` path **self-anchors its
> startup baseline**, so the prefill does not leak into its sync error — that failure mode lives
> only on the external `ReadRaw` + sample-source path (our `BufferedAudioSampleSource`). Honestly
> reproducing it requires driving `BufferedAudioSampleSource`, which is deferred to a follow-up.
> These buffer-level tests cover what is provable here: no spurious correction on a steady stream,
> and drift detection with our options. The gate's default-off safety is covered directly by
> `UseAudioClockGateTests`.

### `UseAudioClockGateTests` — gate default (small pure guard)
- `ParseUseAudioClock`: `null` / empty / unset / `"false"` → **false** (safety default);
  `"true"` / `"1"` / `"yes"` (any case) → true. A regression to default-on would silently
  restore the multi-room drift, so this property is worth pinning.

## Coverage map

| Change | Guarded by |
|---|---|
| Offset sign inversion | `DelayOffsetConventionTests` |
| Re-anchor on offset change | `StaticDelayReanchorTests` |
| Audio clock default-off | `UseAudioClockGateTests` + `SyncAlignmentTests` (prefill drift) |
| `PulseAudioSyncOptions` tuning | `SyncAlignmentTests` (schedule holds) |

## CI integration

Add a `dotnet test` step. `lint.yml` already sets up .NET 8 and restores/builds the app;
the test project slots in after the build. Runs on Linux (LF line endings) where the format
gate already passes.

**Open item for spec review:** wire CI now (this change) vs. a follow-up PR.

## Risks / notes

- The test project references a `Microsoft.NET.Sdk.Web` project; `PublishSingleFile` /
  `SelfContained` only affect publish, not the reference, so the test build is unaffected.
- SDK primitive API surface (`TimedAudioBuffer`, `IClockSynchronizer`, `SyncCorrectionOptions`,
  `AudioFormat`) is taken from windowsSpin's existing tests against the same 9.x package.
