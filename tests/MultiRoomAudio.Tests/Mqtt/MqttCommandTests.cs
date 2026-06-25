using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttCommandTests
{
    [Fact]
    public void Parse_OffsetCommand()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/player/abc123/offset/set", "250");
        Assert.Equal(MqttCommandKind.PlayerOffset, c.Kind);
        Assert.Equal("abc123", c.PlayerClientId);
        Assert.Equal(250, c.IntValue);
    }

    [Fact]
    public void Parse_RestartCommand()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/player/abc123/restart/set", "PRESS");
        Assert.Equal(MqttCommandKind.PlayerRestart, c.Kind);
        Assert.Equal("abc123", c.PlayerClientId);
    }

    [Fact]
    public void Parse_UnknownTopic_ReturnsUnknown()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/other/thing", "x");
        Assert.Equal(MqttCommandKind.Unknown, c.Kind);
    }

    [Fact]
    public void Parse_OffsetWithBadPayload_HasNullValue()
    {
        var c = MqttCommand.Parse("multiroom-audio", "multiroom-audio/player/abc123/offset/set", "abc");
        Assert.Equal(MqttCommandKind.PlayerOffset, c.Kind);
        Assert.Null(c.IntValue);
    }
}
