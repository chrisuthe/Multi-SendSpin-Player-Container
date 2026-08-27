using Microsoft.Extensions.Logging.Abstractions;
using MultiRoomAudio.Utilities;
using Sendspin.SDK.Synchronization;
using Xunit;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Guards our output-delay sign convention against the real SDK clock. Per the Sendspin spec a
/// positive <c>output_delay_ms</c> compensates for downstream hardware by scheduling audio EARLIER.
/// These run through our real conversion seam and a real <see cref="KalmanClockSynchronizer"/>, so
/// they catch a future SDK sign flip rather than letting it silently invert every player's delay.
/// </summary>
/// <remarks>
/// This replaces an earlier convention where the app's knob meant "positive = play later" and was
/// negated on the way to the SDK. That put a negative <c>output_delay_ms</c> on the wire, which the
/// spec does not permit (range 0-5000, negatives explicitly unsupported).
/// </remarks>
public class DelayOffsetConventionTests
{
    private const long ServerTime = 1_000_000_000L;

    private static long ShiftFor(int outputDelayMs)
    {
        var clock = new KalmanClockSynchronizer(NullLogger<KalmanClockSynchronizer>.Instance);

        clock.StaticDelayMs = 0;
        var baseline = clock.ServerToClientTime(ServerTime);

        clock.StaticDelayMs = OutputDelay.ToStaticDelayMs(outputDelayMs);
        return clock.ServerToClientTime(ServerTime) - baseline;
    }

    /// <summary>
    /// The spec's meaning: a 200ms output delay says "my amp adds 200ms", so playback is scheduled
    /// 200ms earlier to land on time.
    /// </summary>
    [Fact]
    public void PositiveOutputDelay_SchedulesEarlier()
    {
        Assert.Equal(-200_000, ShiftFor(200)); // microseconds
    }

    [Fact]
    public void ZeroOutputDelay_DoesNotShiftPlayback()
    {
        Assert.Equal(0, ShiftFor(0));
    }

    [Fact]
    public void OutputDelay_ShiftsProportionally()
    {
        Assert.Equal(-1_000_000, ShiftFor(1000));
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(200, 200.0)]
    [InlineData(5000, 5000.0)]
    public void ToStaticDelayMs_PassesTheSpecValueThrough(int outputDelayMs, double expected)
    {
        Assert.Equal(expected, OutputDelay.ToStaticDelayMs(outputDelayMs));
    }
}
