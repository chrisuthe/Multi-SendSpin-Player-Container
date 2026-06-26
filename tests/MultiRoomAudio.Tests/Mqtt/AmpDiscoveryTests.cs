using System.Linq;
using System.Text.Json;
using MultiRoomAudio.Models;
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class AmpDiscoveryTests
{
    private readonly HaDiscovery _d = new(new MqttTopics("multiroom-audio", "homeassistant"), "1.2.3");

    private static TriggerResponse Zone() => new(
        Channel: 1, CustomSinkName: "sink", CustomSinkDisplayName: "Sink",
        OffDelaySeconds: 30, ZoneName: "Living Room", RelayState: RelayState.On,
        IsActive: true, LastActivated: null, ScheduledOffTime: null, IsOverridden: false);

    [Fact]
    public void Amp_EmitsOverrideSwitchWithCommandTopic()
    {
        var sw = _d.ForAmp("VIRTUAL:x", "Living Room Amp", Zone()).Single(m => m.Topic.Contains("/switch/"));
        using var doc = JsonDocument.Parse(sw.Payload);
        var root = doc.RootElement;
        Assert.Equal("multiroom-audio/amp/virtual_x_1/override/set", root.GetProperty("command_topic").GetString());
        Assert.Equal("multiroom-audio/amp/virtual_x_1/state", root.GetProperty("state_topic").GetString());
    }

    [Fact]
    public void Amp_AllEntitiesShareDeviceIdentifier()
    {
        var ids = _d.ForAmp("VIRTUAL:x", "Living Room Amp", Zone()).Select(m =>
        {
            using var doc = JsonDocument.Parse(m.Payload);
            return doc.RootElement.GetProperty("device").GetProperty("identifiers")[0].GetString();
        }).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal("mra_amp_virtual_x_1", ids[0]);
    }

    [Fact]
    public void Amp_EmitsPowerBinarySensor()
        => Assert.Contains(_d.ForAmp("VIRTUAL:x", null, Zone()), m => m.Topic.Contains("/binary_sensor/") && m.Payload.Contains("value_json.power"));
}
