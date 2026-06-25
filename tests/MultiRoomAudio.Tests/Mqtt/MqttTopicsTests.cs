using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttTopicsTests
{
    private readonly MqttTopics _t = new("multiroom-audio", "homeassistant");

    [Fact]
    public void BridgeAvailability_IsStable()
        => Assert.Equal("multiroom-audio/bridge/availability", _t.BridgeAvailabilityTopic);

    [Fact]
    public void PlayerState_UsesSanitizedClientId()
        => Assert.Equal("multiroom-audio/player/abc123/state", _t.PlayerStateTopic("ABC123"));

    [Fact]
    public void PlayerCommand_AppendsSetSuffix()
        => Assert.Equal("multiroom-audio/player/abc123/offset/set", _t.PlayerCommandTopic("abc123", "offset"));

    [Fact]
    public void Discovery_FollowsHaConvention()
        => Assert.Equal("homeassistant/sensor/mra_x/config", _t.DiscoveryTopic("sensor", "mra_x"));

    [Fact]
    public void Sanitize_ReplacesIllegalChars()
        => Assert.Equal("living_room_2", MqttTopics.Sanitize("Living Room #2"));
}
