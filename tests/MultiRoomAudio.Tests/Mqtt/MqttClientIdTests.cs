using MultiRoomAudio.Services;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttClientIdTests
{
    [Fact]
    public void NewClientId_HasExpectedPrefix()
        => Assert.StartsWith("multiroom-audio-", MqttService.NewClientId());

    [Fact]
    public void NewClientId_IsUniquePerCall()
    {
        // Two clients (stale broker session, second instance, host_network reuse)
        // must never share an ID, or the broker kicks one off in a takeover loop.
        Assert.NotEqual(MqttService.NewClientId(), MqttService.NewClientId());
    }
}
