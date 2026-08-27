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
/// These tests exercise the same <c>Read</c> path the app uses. The buffer owns the whole correction
/// loop: it measures the error and closes it itself with frame drop/insert inside the spec's
/// +/-0.5% speed cap, escalating to a one-shot snap above ~5ms. <c>TargetPlaybackRate</c> therefore
/// stays 1.0 throughout - correction is expressed as frame stepping, not as a rate for a resampler
/// we do not have - so these tests assert on the outcome (final sync error and the correction
/// actually applied) rather than on a recommended rate.
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

    private readonly record struct SessionResult(
        double FinalSyncErrorMs,
        long NetCorrectionSamples,
        long TotalSamplesOutput,
        double TargetPlaybackRate)
    {
        /// <summary>Correction applied, as a fraction of all samples output.</summary>
        public double CorrectionFraction => Math.Abs(NetCorrectionSamples) / (double)TotalSamplesOutput;
    }

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
        var totalOutput = (long)totalReads * ChunkSamples;

        var result = new SessionResult(finalErrMs, net, totalOutput, buffer.TargetPlaybackRate);

        _output.WriteLine(
            $"drift={clockDriftFactor:F3} -> inserted={stats.SamplesInsertedForSync} " +
            $"dropped={stats.SamplesDroppedForSync} net={net} " +
            $"correction={result.CorrectionFraction:P3} of {totalOutput} samples " +
            $"rate={buffer.TargetPlaybackRate:F4} finalErr={finalErrMs:F1}ms");

        return result;
    }

    // Margin covering the harness's one-callback (10ms) granularity floor.
    private const double InSyncToleranceMs = 15.0;

    /// <summary>
    /// Control: a perfectly drift-free session holds the server schedule and the corrector does not
    /// hunt - any correction it applies stays a negligible fraction of the stream. Guards against a
    /// sync-options regression that would make a steady stream over-correct (audible artifacts).
    /// </summary>
    [Fact]
    public void DriftFree_HoldsSchedule_WithoutHunting()
    {
        var result = RunSession(clockDriftFactor: 1.0, seconds: 20);

        Assert.True(
            Math.Abs(result.FinalSyncErrorMs) < InSyncToleranceMs,
            $"drift-free playback should hold the schedule, but settled {result.FinalSyncErrorMs:F1}ms off");

        // A drift-free stream needs at most a one-off startup alignment. Sustained correction on a
        // steady stream is hunting; the spec's own cap is 0.5% of samples, so anything at or above
        // that is the corrector working continuously rather than settling.
        Assert.True(
            result.CorrectionFraction < 0.005,
            $"drift-free playback should not hunt, but corrected {result.CorrectionFraction:P3} of the stream");
    }

    /// <summary>
    /// A genuine playback-clock drift is corrected, not merely detected: the buffer drops samples to
    /// track the fast clock and brings the session back onto the server schedule. Guards that our
    /// sync options actually close the loop on real drift.
    /// </summary>
    [Fact]
    public void ClockDrift_IsCorrected_BackOntoSchedule()
    {
        var result = RunSession(clockDriftFactor: 1.01, seconds: 20); // 1% fast playback clock

        Assert.True(
            Math.Abs(result.FinalSyncErrorMs) < InSyncToleranceMs,
            $"a 1% clock drift should be corrected back onto schedule, got {result.FinalSyncErrorMs:F1}ms");

        // A 1% fast playback clock consumes audio faster than the schedule, so the buffer must DROP
        // to keep up - net correction is negative and tracks the drift magnitude.
        Assert.True(
            result.NetCorrectionSamples < 0,
            $"a fast playback clock should drop samples, but net correction was {result.NetCorrectionSamples}");
        Assert.InRange(result.CorrectionFraction, 0.005, 0.02); // ~1% drift, generous either side
    }
}
