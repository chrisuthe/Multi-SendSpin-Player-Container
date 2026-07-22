using System.Linq;
using System.Text.Json;
using MultiRoomAudio.Models;
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class HaDiscoveryTests
{
    private readonly HaDiscovery _d = new(new MqttTopics("multiroom-audio", "homeassistant"), "1.2.3");

    private static PlayerResponse Player() => new(
        "Living Room", PlayerState.Playing, "dac0", "abc123", "http://ma",
        "Music Assistant", "192.168.1.50:8095", 50, 40, false, 0, 10,
        DateTime.UnixEpoch, DateTime.UnixEpoch, null, true, null,
        IsPendingReconnection: false, ReconnectionAttempts: 0);

    private static JsonElement DeviceMessage(System.Collections.Generic.IReadOnlyList<HaDiscovery.DiscoveryMessage> msgs)
    {
        var device = msgs.Single(m => !string.IsNullOrEmpty(m.Payload));
        return JsonDocument.Parse(device.Payload).RootElement.Clone();
    }

    [Fact]
    public void Player_EmitsRestartButtonWithCommandTopic()
    {
        var root = DeviceMessage(_d.ForPlayerDevice(Player()));
        var components = root.GetProperty("components");
        var restart = components.EnumerateObject().Single(e => e.Value.GetProperty("p").GetString() == "button");

        Assert.Equal("multiroom-audio/player/abc123/restart/set", restart.Value.GetProperty("command_topic").GetString());
        Assert.Equal("multiroom-audio/bridge/availability", root.GetProperty("availability_topic").GetString());
        Assert.StartsWith("mra_player_abc123_", restart.Value.GetProperty("unique_id").GetString());
    }

    [Fact]
    public void Player_OffsetNumberHasCommandTopicAndRange()
    {
        var root = DeviceMessage(_d.ForPlayerDevice(Player()));
        var components = root.GetProperty("components");
        var offset = components.EnumerateObject().Single(e => e.Value.GetProperty("p").GetString() == "number");

        Assert.Equal("multiroom-audio/player/abc123/offset/set", offset.Value.GetProperty("command_topic").GetString());
        Assert.Equal(-5000, offset.Value.GetProperty("min").GetInt32());
        Assert.Equal(5000, offset.Value.GetProperty("max").GetInt32());
    }

    [Fact]
    public void Player_AllEntitiesShareDeviceIdentifier()
    {
        var root = DeviceMessage(_d.ForPlayerDevice(Player()));
        Assert.Equal("mra_player_abc123", root.GetProperty("dev").GetProperty("ids").GetString());

        var components = root.GetProperty("components");
        Assert.All(components.EnumerateObject(), e =>
            Assert.StartsWith("mra_player_abc123_", e.Value.GetProperty("unique_id").GetString()));
    }

    [Fact]
    public void Container_EmitsReadyBinarySensor()
    {
        var root = DeviceMessage(_d.ForContainerDevice("instance1"));
        var components = root.GetProperty("components");

        Assert.Contains(components.EnumerateObject(), e =>
            e.Value.GetProperty("p").GetString() == "binary_sensor" &&
            e.Value.GetProperty("value_template").GetString()!.Contains("value_json.ready"));
    }
}
