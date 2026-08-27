using Microsoft.Extensions.Logging.Abstractions;
using MultiRoomAudio.Services;
using Xunit;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Covers the one-way migration of saved players onto the spec's <c>output_delay_ms</c>, including
/// that a second load leaves an already-migrated player alone.
/// </summary>
public class OutputDelayMigrationTests
{
    private static Dictionary<string, PlayerConfiguration> Players(params (string Name, int Legacy, int? Output)[] rows)
    {
        var d = new Dictionary<string, PlayerConfiguration>();
        foreach (var (name, legacy, output) in rows)
        {
            d[name] = new PlayerConfiguration { Name = name, DelayMs = legacy, OutputDelayMs = output };
        }
        return d;
    }

    private static void Migrate(Dictionary<string, PlayerConfiguration> players)
        => ConfigurationService.MigrateOutputDelays(players, NullLogger.Instance);

    [Fact]
    public void EarlyPlayingPlayer_KeepsItsBehaviour()
    {
        var players = Players(("Kitchen", -200, null));

        Migrate(players);

        Assert.Equal(200, players["Kitchen"].OutputDelayMs);
    }

    [Fact]
    public void LatePlayingPlayer_SettlesAtNoDelay()
    {
        var players = Players(("Patio", 200, null));

        Migrate(players);

        Assert.Equal(0, players["Patio"].OutputDelayMs);
    }

    /// <summary>
    /// The legacy field is cleared so a migrated file never carries a stale value that contradicts
    /// the migrated one.
    /// </summary>
    [Fact]
    public void Migration_ClearsTheLegacyField()
    {
        var players = Players(("Kitchen", -200, null));

        Migrate(players);

        Assert.Equal(0, players["Kitchen"].DelayMs);
    }

    [Fact]
    public void AlreadyMigratedPlayer_IsLeftAlone()
    {
        var players = Players(("Office", 0, 750));

        Migrate(players);

        Assert.Equal(750, players["Office"].OutputDelayMs);
    }

    [Fact]
    public void RepeatedMigration_IsStable()
    {
        var players = Players(("Kitchen", -200, null), ("Patio", 200, null), ("Office", -5000, null));

        Migrate(players);
        var afterFirst = players.ToDictionary(kv => kv.Key, kv => kv.Value.OutputDelayMs);
        Migrate(players);
        Migrate(players);

        Assert.Equal(afterFirst, players.ToDictionary(kv => kv.Key, kv => kv.Value.OutputDelayMs));
        Assert.Equal(200, players["Kitchen"].OutputDelayMs);
        Assert.Equal(0, players["Patio"].OutputDelayMs);
        Assert.Equal(5000, players["Office"].OutputDelayMs);
    }

    [Fact]
    public void EmptyConfig_IsHandled()
    {
        var players = Players();

        Migrate(players);

        Assert.Empty(players);
    }
}
