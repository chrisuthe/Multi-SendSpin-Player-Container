using System.Collections.Generic;
using System.Text.Json;
using MultiRoomAudio.Models;
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class AmpStatePayloadsTests
{
    private static TriggerResponse Zone(RelayState s, bool overridden) => new(
        Channel: 1, CustomSinkNames: new List<string> { "sink" },
        CustomSinkDisplayNames: new List<string> { "Sink" },
        OffDelaySeconds: 30, ZoneName: "Living Room", RelayState: s,
        IsActive: s == RelayState.On, LastActivated: null, ScheduledOffTime: null,
        IsOverridden: overridden);

    [Fact]
    public void Amp_SerializesPowerOverrideAndConnectivity()
    {
        using var doc = JsonDocument.Parse(MqttStatePayloads.Amp(Zone(RelayState.On, overridden: true), boardConnected: true));
        var root = doc.RootElement;
        Assert.Equal("ON", root.GetProperty("power").GetString());
        Assert.Equal("ON", root.GetProperty("override").GetString());
        Assert.Equal("ON", root.GetProperty("board_connected").GetString());
    }

    [Fact]
    public void Amp_PowerOff_WhenRelayOff()
    {
        using var doc = JsonDocument.Parse(MqttStatePayloads.Amp(Zone(RelayState.Off, overridden: false), boardConnected: false));
        var root = doc.RootElement;
        Assert.Equal("OFF", root.GetProperty("power").GetString());
        Assert.Equal("OFF", root.GetProperty("override").GetString());
        Assert.Equal("OFF", root.GetProperty("board_connected").GetString());
    }
}
