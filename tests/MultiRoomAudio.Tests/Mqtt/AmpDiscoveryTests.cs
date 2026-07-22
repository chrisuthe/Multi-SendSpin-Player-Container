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

    private static JsonElement DeviceMessage(System.Collections.Generic.IReadOnlyList<HaDiscovery.DiscoveryMessage> msgs)
    {
        var device = msgs.Single(m => !string.IsNullOrEmpty(m.Payload));
        return JsonDocument.Parse(device.Payload).RootElement.Clone();
    }

    [Fact]
    public void Amp_EmitsOverrideSwitchWithCommandTopic()
    {
        var root = DeviceMessage(_d.ForAmpDevice("VIRTUAL:x", "Living Room Amp", Zone()));
        var components = root.GetProperty("components");
        var sw = components.EnumerateObject().Single(e => e.Value.GetProperty("p").GetString() == "switch");

        Assert.Equal("multiroom-audio/amp/virtual_x_1/override/set", sw.Value.GetProperty("command_topic").GetString());
        Assert.Equal("multiroom-audio/amp/virtual_x_1/state", root.GetProperty("state_topic").GetString());
    }

    [Fact]
    public void Amp_AllEntitiesShareDeviceIdentifier()
    {
        var root = DeviceMessage(_d.ForAmpDevice("VIRTUAL:x", "Living Room Amp", Zone()));
        Assert.Equal("mra_amp_virtual_x_1", root.GetProperty("dev").GetProperty("ids").GetString());

        var components = root.GetProperty("components");
        Assert.All(components.EnumerateObject(), e =>
            Assert.StartsWith("mra_amp_virtual_x_1_", e.Value.GetProperty("unique_id").GetString()));
    }

    [Fact]
    public void Amp_EmitsPowerBinarySensor()
        => Assert.Contains(_d.ForAmpDevice("VIRTUAL:x", null, Zone()), m => m.Payload.Contains("value_json.power"));
}
