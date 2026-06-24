using MultiRoomAudio.Services;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Xunit;
using Xunit.Abstractions;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Boxes the sync invariant at the buffer level, with no audio hardware, using our real
/// <see cref="PlayerManagerService.PulseAudioSyncOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Multi-room sync reduces to a single-player property: a player is in sync iff it outputs sample-T
/// at <c>ServerToClientTime(T)</c>. We drive the real <see cref="TimedAudioBuffer"/> through a
/// simulated session and read at a simulated playback clock.
/// </para>
/// <para>
/// Note: the buffer's internal <c>Read</c> path self-anchors its startup baseline, so an output
/// prefill does not leak into its sync error — that failure mode lives on the external
/// <c>ReadRaw</c> + sample-source path (our <c>BufferedAudioSampleSource</c>) and is why
/// <c>USE_AUDIO_CLOCK</c> defaults off. These tests therefore cover what the buffer level can prove:
/// our sync options hold a drift-free schedule without hunting, and correct a genuine drift.
/// </para>
/// </remarks>
public class SyncAlignmentTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int ChunkFrames = 480; // 10ms callback
    private const int ChunkSamples = ChunkFrames * Channels;
    private const double UsPerFrame = 1_000_000.0 / SampleRate;
    private const long VirtualStart = 1_000_000_000L;

    private readonly ITestOutputHelper _output;

    public SyncAlignmentTests(ITestOutputHelper output) => _output = output;

    private readonly record struct SessionResult(double FinalSyncErrorMs, long NetCorrectionSamples, double TargetPlaybackRate);

    /// <param name="clockDriftFactor">
    /// Playback-clock rate relative to the server schedule. 1.0 = perfect; 1.01 = clock runs 1% fast.
    /// </param>
    private SessionResult RunSession(double clockDriftFactor, int seconds)
    {
        var format = new AudioFormat { Codec = "pcm", SampleRate = SampleRate, Channels = Channels, BitDepth = 32 };
        var clock = new FakeClock(VirtualStart); // ServerToClientTime(0) == VirtualStart
        using var buffer = new TimedAudioBuffer(
            format, clock, bufferCapacityMs: 4000, syncOptions: PlayerManagerService.PulseAudioSyncOptions);

        var data = new float[ChunkSamples];
        Array.Fill(data, 0.25f);
        var outBuf = new float[ChunkSamples];
        long framesWritten = 0;

        void WriteChunk()
        {
            buffer.Write(data, (long)(framesWritten * UsPerFrame));
            framesWritten += ChunkFrames;
        }

        for (var k = 0; k < 100; k++) // pre-fill ~1000ms so reads never underrun
        {
            WriteChunk();
        }

        var totalReads = seconds * 100; // 100 × 10ms callbacks per second
        for (var i = 0; i < totalReads; i++)
        {
            // Playback clock advances with the consumed audio, scaled by the drift factor.
            var scheduledElapsedUs = (long)(i + 1) * ChunkFrames * UsPerFrame;
            var nowMicros = VirtualStart + (long)(scheduledElapsedUs * clockDriftFactor);

            buffer.Read(outBuf, nowMicros);
            WriteChunk(); // keep the buffer topped up
        }

        var stats = buffer.GetStats();
        var net = stats.SamplesInsertedForSync - stats.SamplesDroppedForSync;
        var finalErrMs = buffer.SyncErrorMicroseconds / 1000.0;

        _output.WriteLine(
            $"drift={clockDriftFactor:F3} -> inserted={stats.SamplesInsertedForSync} " +
            $"dropped={stats.SamplesDroppedForSync} net={net} rate={buffer.TargetPlaybackRate:F4} " +
            $"finalErr={finalErrMs:F1}ms");

        return new SessionResult(finalErrMs, net, buffer.TargetPlaybackRate);
    }

    // Margin covering the harness's one-callback (10ms) granularity floor.
    private const double InSyncToleranceMs = 15.0;

    /// <summary>
    /// Control: a perfectly drift-free session holds the server schedule (sync error ≈ 0) and the
    /// corrector does not hunt — zero net drop/insert correction. Guards against a sync-options
    /// regression that would make a steady stream over-correct (audible artifacts, drift off sync).
    /// </summary>
    [Fact]
    public void DriftFree_HoldsSchedule_WithNoSpuriousCorrection()
    {
        var result = RunSession(clockDriftFactor: 1.0, seconds: 20);

        Assert.True(
            Math.Abs(result.FinalSyncErrorMs) < InSyncToleranceMs,
            $"drift-free playback should hold the schedule, but settled {result.FinalSyncErrorMs:F1}ms off");
        Assert.Equal(0, result.NetCorrectionSamples);
        Assert.Equal(1.0, result.TargetPlaybackRate); // no correction recommended
    }

    /// <summary>
    /// A genuine playback-clock drift is detected and the buffer recommends a rate correction
    /// (<c>TargetPlaybackRate</c> moves off 1.0) while tracking a non-zero sync error. Guards that our
    /// sync options actually track real drift. (Closing the correction loop — resampling to the
    /// recommended rate — is the sample-source's job, exercised by the app, not this buffer harness.)
    /// </summary>
    [Fact]
    public void ClockDrift_IsDetected_AndRateCorrectionRecommended()
    {
        var result = RunSession(clockDriftFactor: 1.01, seconds: 20); // 1% fast playback clock

        Assert.NotEqual(1.0, result.TargetPlaybackRate);
        Assert.True(
            Math.Abs(result.FinalSyncErrorMs) > InSyncToleranceMs,
            $"a 1% clock drift should register a clear sync error, got {result.FinalSyncErrorMs:F1}ms");
    }
}
