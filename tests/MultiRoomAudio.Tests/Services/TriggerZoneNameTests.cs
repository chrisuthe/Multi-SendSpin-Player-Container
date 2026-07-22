using System.Collections.Generic;
using System.Linq;
using MultiRoomAudio.Models;
using MultiRoomAudio.Relay;
using MultiRoomAudio.Services;
using Xunit;

namespace MultiRoomAudio.Tests.Services;

/// <summary>
/// ZoneName is set only through the API and read only by HA MQTT discovery (it names the
/// amplifier-zone device and its entities). The web UI never sends it, so ConfigureTrigger
/// must treat an absent zoneName as "leave unchanged" rather than "clear".
/// </summary>
public class TriggerZoneNameTests
{
    private static (TriggerService svc, string boardId) Setup()
    {
        var svc = TriggerTestHarness.CreateMockService();
        svc.SetEnabled(true);
        var boardId = "VIRTUAL:zone01";
        svc.AddBoard(boardId, "Test", channelCount: 2, boardType: RelayBoardType.Virtual);
        return (svc, boardId);
    }

    private static TriggerResponse Channel(TriggerService svc, string boardId, int channel)
        => svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == channel);

    [Fact]
    public void NullZoneName_PreservesExistingName()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 1, new List<string> { "zoneA" }, 30, "Living Room");

        // Mimics a UI save: sinks and delay only, no zoneName key in the JSON body.
        svc.ConfigureTrigger(boardId, 1, new List<string> { "zoneA", "zoneB" }, 45, null);

        Assert.Equal("Living Room", Channel(svc, boardId, 1).ZoneName);
    }

    [Fact]
    public void EmptyZoneName_ClearsExistingName()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 1, new List<string> { "zoneA" }, 30, "Living Room");

        svc.ConfigureTrigger(boardId, 1, new List<string> { "zoneA" }, 30, "");

        Assert.Null(Channel(svc, boardId, 1).ZoneName);
    }

    [Fact]
    public void NonEmptyZoneName_OverwritesExistingName()
    {
        var (svc, boardId) = Setup();
        svc.ConfigureTrigger(boardId, 1, new List<string> { "zoneA" }, 30, "Living Room");

        svc.ConfigureTrigger(boardId, 1, new List<string> { "zoneA" }, 30, "Kitchen");

        Assert.Equal("Kitchen", Channel(svc, boardId, 1).ZoneName);
    }
}
