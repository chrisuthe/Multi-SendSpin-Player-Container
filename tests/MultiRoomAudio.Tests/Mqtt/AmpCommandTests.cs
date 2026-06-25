using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class AmpCommandTests
{
    [Fact]
    public void Parse_AmpOverrideOn()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/amp/virtual_x_1/override/set", "ON");
        Assert.Equal(MqttCommandKind.AmpOverride, c.Kind);
        Assert.Equal("virtual_x_1", c.AmpZone);
        Assert.True(c.BoolValue);
    }

    [Fact]
    public void Parse_AmpOverrideOff()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/amp/virtual_x_1/override/set", "OFF");
        Assert.Equal(MqttCommandKind.AmpOverride, c.Kind);
        Assert.False(c.BoolValue);
    }

    [Fact]
    public void Parse_PlayerOffset_StillWorks()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/player/abc123/offset/set", "250");
        Assert.Equal(MqttCommandKind.PlayerOffset, c.Kind);
        Assert.Equal(250, c.IntValue);
    }
}
