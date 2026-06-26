using Microsoft.Extensions.Logging.Abstractions;
using MultiRoomAudio.Services;
using Sendspin.SDK.Synchronization;
using Xunit;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Guards our delay-offset sign convention: a positive user "Delay Offset" must play LATER, which
/// (after the SDK v8.0.0 static_delay sign flip) means a negative StaticDelayMs. These run through
/// our real conversion helper and the real SDK clock, so they also catch a future SDK sign flip.
/// </summary>
public class DelayOffsetConventionTests
{
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(200, -200.0)]
    [InlineData(-200, 200.0)]
    [InlineData(5000, -5000.0)]
    public void UserDelayToStaticDelayMs_NegatesValue(int userDelay, double expectedStaticDelay)
    {
        Assert.Equal(expectedStaticDelay, PlayerManagerService.UserDelayToStaticDelayMs(userDelay));
    }

    [Fact]
    public void PositiveOffset_SchedulesLater()
    {
        var clock = new KalmanClockSynchronizer(NullLogger<KalmanClockSynchronizer>.Instance);
        const long serverTime = 1_000_000_000L;

        clock.StaticDelayMs = 0;
        var baseline = clock.ServerToClientTime(serverTime);

        clock.StaticDelayMs = PlayerManagerService.UserDelayToStaticDelayMs(200); // +200ms "later"
        var delayed = clock.ServerToClientTime(serverTime);

        // A positive user offset must schedule playback LATER: a larger client time, by 200ms.
        Assert.Equal(200_000, delayed - baseline); // microseconds
    }

    [Fact]
    public void NegativeOffset_SchedulesEarlier()
    {
        var clock = new KalmanClockSynchronizer(NullLogger<KalmanClockSynchronizer>.Instance);
        const long serverTime = 1_000_000_000L;

        clock.StaticDelayMs = 0;
        var baseline = clock.ServerToClientTime(serverTime);

        clock.StaticDelayMs = PlayerManagerService.UserDelayToStaticDelayMs(-200); // -200ms "earlier"
        var earlier = clock.ServerToClientTime(serverTime);

        Assert.Equal(-200_000, earlier - baseline);
    }
}
