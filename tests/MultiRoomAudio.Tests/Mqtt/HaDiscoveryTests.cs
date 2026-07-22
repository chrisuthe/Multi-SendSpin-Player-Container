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

    // Finds a component within a device payload by the tail of its unique_id (e.g. "version").
    private static JsonElement Component(JsonElement device, string keySuffix)
        => device.GetProperty("components").EnumerateObject()
            .Single(e => e.Value.GetProperty("unique_id").GetString()!
                .EndsWith($"_{keySuffix}", System.StringComparison.OrdinalIgnoreCase))
            .Value;

    [Theory]
    [InlineData("version")]
    [InlineData("audio_backend")]
    [InlineData("environment")]
    public void Container_DiagnosticSensorsDisabledByDefault(string key)
    {
        var device = DeviceMessage(_d.ForContainerDevice("instance1"));
        var component = Component(device, key);
        Assert.False(component.GetProperty("enabled_by_default").GetBoolean());
    }

    [Theory]
    // #249/#257: player diagnostic entities register but stay disabled to keep HA's recorder
    // workload down; the bridge pushes state frequently and these are rarely watched.
    [InlineData("server")]
    [InlineData("clock_synced")]
    [InlineData("reconnect_pending")]
    [InlineData("reconnect_attempts")]
    public void Player_DiagnosticSensorsDisabledByDefault(string key)
    {
        var device = DeviceMessage(_d.ForPlayerDevice(Player()));
        var component = Component(device, key);
        Assert.Equal("diagnostic", component.GetProperty("entity_category").GetString());
        Assert.False(component.GetProperty("enabled_by_default").GetBoolean());
    }

    [Theory]
    // Controls stay enabled - a restart button or offset control nobody can see defeats its
    // purpose. State is the primary sensor and is also enabled.
    [InlineData("State")]
    [InlineData("offset")]
    [InlineData("restart")]
    public void Player_ControlsAndStateRemainEnabled(string key)
    {
        var device = DeviceMessage(_d.ForPlayerDevice(Player()));
        var component = Component(device, key);
        Assert.False(component.TryGetProperty("enabled_by_default", out var v) && !v.GetBoolean(),
            $"component '{key}' should not be disabled by default");
    }

    [Fact]
    public void Player_StateSensorIsEnumWithAllPlayerStateOptions()
    {
        var device = DeviceMessage(_d.ForPlayerDevice(Player()));
        var state = Component(device, "State");
        Assert.Equal("enum", state.GetProperty("device_class").GetString());

        var options = state.GetProperty("options").EnumerateArray().Select(e => e.GetString()!).ToList();
        var expected = Enum.GetNames<PlayerState>().Select(n => n.ToLowerInvariant()).ToList();
        Assert.Equal(expected, options);
    }
}
