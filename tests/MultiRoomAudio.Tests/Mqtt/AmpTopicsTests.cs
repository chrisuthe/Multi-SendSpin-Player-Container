using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class AmpTopicsTests
{
    private readonly MqttTopics _t = new("multiroom-audio", "homeassistant");

    [Fact]
    public void AmpState_UsesSanitizedBoardAndChannel()
        => Assert.Equal("multiroom-audio/amp/virtual_x_1/state", _t.AmpStateTopic("VIRTUAL:x", 1));

    [Fact]
    public void AmpCommand_AppendsSetSuffix()
        => Assert.Equal("multiroom-audio/amp/virtual_x_1/override/set", _t.AmpCommandTopic("VIRTUAL:x", 1, "override"));

    [Fact]
    public void AmpCommandSubscription_IsWildcard()
        => Assert.Equal("multiroom-audio/amp/+/+/set", _t.AmpCommandSubscription);
}
