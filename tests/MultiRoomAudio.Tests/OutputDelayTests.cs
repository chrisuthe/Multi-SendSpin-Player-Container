using MultiRoomAudio.Utilities;
using Xunit;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Pins <c>output_delay_ms</c> to the Sendspin spec: an integer in 0-5000 where positive schedules
/// audio earlier to compensate downstream hardware. Guards the range the app puts on the wire, and
/// the one-way migration off the old "positive = play later" convention.
/// </summary>
public class OutputDelayTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(200, 200)]
    [InlineData(5000, 5000)]
    [InlineData(-1, 0)]
    [InlineData(-5000, 0)]
    [InlineData(5001, 5000)]
    [InlineData(int.MaxValue, 5000)]
    [InlineData(int.MinValue, 0)]
    public void Clamp_HoldsTheSpecRange(int input, int expected)
    {
        Assert.Equal(expected, OutputDelay.Clamp(input));
    }

    /// <summary>
    /// A player saved as "play 200ms early" keeps playing 200ms early. The old value was negated
    /// on the way to the SDK, so its negation is the behaviour-preserving output delay.
    /// </summary>
    [Fact]
    public void MigrateLegacyDelay_PreservesAnEarlyPlayingPlayer()
    {
        Assert.Equal(200, OutputDelay.MigrateLegacyDelay(-200));
    }

    /// <summary>
    /// "Play later" has no expression under the spec, so those players settle at no delay rather
    /// than silently inverting into an early-playing one.
    /// </summary>
    [Fact]
    public void MigrateLegacyDelay_DropsAnUnexpressibleLatePlayingPlayer()
    {
        Assert.Equal(0, OutputDelay.MigrateLegacyDelay(200));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5000, 5000)]
    [InlineData(-9999, 5000)]
    public void MigrateLegacyDelay_StaysInRange(int legacy, int expected)
    {
        Assert.Equal(expected, OutputDelay.MigrateLegacyDelay(legacy));
    }

    [Fact]
    public void ResolvePersisted_MigratesWhenNeverWritten()
    {
        Assert.Equal(200, OutputDelay.ResolvePersisted(outputDelayMs: null, legacyDelayMs: -200));
    }

    [Fact]
    public void ResolvePersisted_IgnoresLegacyOnceMigrated()
    {
        Assert.Equal(750, OutputDelay.ResolvePersisted(outputDelayMs: 750, legacyDelayMs: -200));
    }

    /// <summary>
    /// The case that makes migration safe to re-run: a player already migrated to 0 must not be
    /// re-derived from its stale legacy field, or every restart would walk its delay.
    /// </summary>
    [Fact]
    public void ResolvePersisted_TreatsMigratedZeroAsMigrated()
    {
        Assert.Equal(0, OutputDelay.ResolvePersisted(outputDelayMs: 0, legacyDelayMs: -200));
    }

    [Fact]
    public void ResolvePersisted_IsIdempotentAcrossRepeatedLoads()
    {
        const int legacy = -200;
        var first = OutputDelay.ResolvePersisted(null, legacy);
        var second = OutputDelay.ResolvePersisted(first, legacy);
        var third = OutputDelay.ResolvePersisted(second, legacy);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void ResolvePersisted_ClampsAnOutOfRangePersistedValue()
    {
        Assert.Equal(5000, OutputDelay.ResolvePersisted(outputDelayMs: 99999, legacyDelayMs: 0));
    }
}
