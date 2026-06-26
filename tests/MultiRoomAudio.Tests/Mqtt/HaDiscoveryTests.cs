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

    [Fact]
    public void Player_EmitsRestartButtonWithCommandTopic()
    {
        var msgs = _d.ForPlayer(Player());
        var button = msgs.Single(m => m.Topic.Contains("/button/"));

        using var doc = JsonDocument.Parse(button.Payload);
        var root = doc.RootElement;
        Assert.Equal("multiroom-audio/player/abc123/restart/set", root.GetProperty("command_topic").GetString());
        Assert.Equal("multiroom-audio/bridge/availability", root.GetProperty("availability_topic").GetString());
        Assert.StartsWith("mra_abc123_", root.GetProperty("unique_id").GetString());
    }

    [Fact]
    public void Player_OffsetNumberHasCommandTopicAndRange()
    {
        var number = _d.ForPlayer(Player()).Single(m => m.Topic.Contains("/number/"));
        using var doc = JsonDocument.Parse(number.Payload);
        var root = doc.RootElement;
        Assert.Equal("multiroom-audio/player/abc123/offset/set", root.GetProperty("command_topic").GetString());
        Assert.Equal(-5000, root.GetProperty("min").GetInt32());
        Assert.Equal(5000, root.GetProperty("max").GetInt32());
    }

    [Fact]
    public void Player_AllEntitiesShareDeviceIdentifier()
    {
        var ids = _d.ForPlayer(Player()).Select(m =>
        {
            using var doc = JsonDocument.Parse(m.Payload);
            return doc.RootElement.GetProperty("device").GetProperty("identifiers")[0].GetString();
        }).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal("mra_player_abc123", ids[0]);
    }

    [Fact]
    public void Container_EmitsReadyBinarySensor()
    {
        var msgs = _d.ForContainer("instance1");
        Assert.Contains(msgs, m =>
        {
            if (!m.Topic.Contains("/binary_sensor/")) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(m.Payload);
            return doc.RootElement.GetProperty("value_template").GetString()!.Contains("value_json.ready");
        });
    }

    [Theory]
    [InlineData("version")]
    [InlineData("audio_backend")]
    [InlineData("environment")]
    public void Container_DiagnosticSensorsDisabledByDefault(string key)
    {
        var msg = _d.ForContainer("instance1").Single(m => m.Topic.Contains($"_{key}/"));
        using var doc = JsonDocument.Parse(msg.Payload);
        Assert.False(doc.RootElement.GetProperty("enabled_by_default").GetBoolean());
    }

    [Fact]
    public void Player_DiagnosticSensorsRemainEnabled()
    {
        // Issue #249 only disables the container version/backend/environment sensors;
        // player diagnostics stay enabled.
        var server = _d.ForPlayer(Player()).Single(m => m.Topic.Contains("_server/"));
        using var doc = JsonDocument.Parse(server.Payload);
        Assert.Equal("diagnostic", doc.RootElement.GetProperty("entity_category").GetString());
        Assert.False(doc.RootElement.TryGetProperty("enabled_by_default", out _));
    }

    [Fact]
    public void Player_StateSensorIsEnumWithAllPlayerStateOptions()
    {
        var state = _d.ForPlayer(Player()).Single(m => m.Topic.Contains("_state/"));
        using var doc = JsonDocument.Parse(state.Payload);
        var root = doc.RootElement;
        Assert.Equal("enum", root.GetProperty("device_class").GetString());

        var options = root.GetProperty("options").EnumerateArray().Select(e => e.GetString()!).ToList();
        var expected = Enum.GetNames<PlayerState>().Select(n => n.ToLowerInvariant()).ToList();
        Assert.Equal(expected, options);
    }
}
