using MultiRoomAudio.Models;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttSettingsTests
{
    [Fact]
    public void Defaults_AreSafeAndDisabled()
    {
        var s = new MqttSettings();

        Assert.False(s.Enabled);
        Assert.Equal(1883, s.Port);
        Assert.False(s.UseTls);
        Assert.Equal("homeassistant", s.DiscoveryPrefix);
        Assert.Equal("multiroom-audio", s.BaseTopic);
        Assert.Null(s.Host);
    }
}
