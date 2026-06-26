using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MultiRoomAudio.Models;
using MultiRoomAudio.Relay;
using MultiRoomAudio.Services;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Tests.Services;

public class MultiSinkTriggerMigrationTests
{
    private static IDeserializer Deserializer() => new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static ISerializer Serializer() => new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    [Fact]
    public void LegacySingleSink_MigratesIntoList()
    {
        var yaml = "channel: 1\ncustom_sink_name: sink1\noff_delay_seconds: 30\n";
        var cfg = Deserializer().Deserialize<TriggerConfiguration>(yaml);
        Assert.Equal(new[] { "sink1" }, cfg.CustomSinkNames);
    }

    [Fact]
    public void Serialization_OmitsLegacyKey_EmitsPluralKey()
    {
        var cfg = new TriggerConfiguration { Channel = 1, CustomSinkNames = { "sink1" } };
        var outYaml = Serializer().Serialize(cfg);
        // Note: "custom_sink_name:" (singular + colon) is NOT a substring of "custom_sink_names:".
        Assert.DoesNotContain("custom_sink_name:", outYaml);
        Assert.Contains("custom_sink_names", outYaml);
    }
}

public class MultiSinkTriggerEngineTests
{
    private static (TriggerService svc, string boardId) Setup()
    {
        var svc = TriggerTestHarness.CreateMockService();
        svc.SetEnabled(true);
        var boardId = "VIRTUAL:multi01";
        svc.AddBoard(boardId, "Test", channelCount: 2, boardType: RelayBoardType.Virtual);
        return (svc, boardId);
    }

    private static TriggerResponse Channel(TriggerService svc, string boardId, int channel)
        => svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == channel);

    [Fact]
    public void OneSinkOnTwoChannels_ActivatesBoth()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 1, new List<string> { "zoneA" }, 30, "Zone A");
        svc.ConfigureTrigger(boardId, 2, new List<string> { "zoneA", "zoneB" }, 30, "Master");

        svc.OnPlayerStarted("p1", "zoneA");

        Assert.Equal(RelayState.On, Channel(svc, boardId, 1).RelayState);
        Assert.Equal(RelayState.On, Channel(svc, boardId, 2).RelayState);
    }

    [Fact]
    public void ChannelWithTwoSinks_StaysOnUntilLastStops()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 2, new List<string> { "zoneA", "zoneB" }, 0, "Master");

        svc.OnPlayerStarted("p1", "zoneA");
        svc.OnPlayerStarted("p2", "zoneB");
        svc.OnPlayerStopped("p1", "zoneA");
        Assert.Equal(RelayState.On, Channel(svc, boardId, 2).RelayState);   // zoneB still active

        svc.OnPlayerStopped("p2", "zoneB");
        Assert.Equal(RelayState.Off, Channel(svc, boardId, 2).RelayState);  // last sink stopped, delay 0 ⇒ immediate off
    }

    [Fact]
    public void DeletingSink_RemovesItFromTriggerLists()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 2, new List<string> { "zoneA", "zoneB" }, 30, "Master");

        svc.OnSinkDeleted("zoneA");

        Assert.Equal(new[] { "zoneB" }, Channel(svc, boardId, 2).CustomSinkNames);
    }
}
