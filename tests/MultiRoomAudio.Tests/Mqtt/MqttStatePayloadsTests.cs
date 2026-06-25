using System.Text.Json;
using MultiRoomAudio.Models;
using MultiRoomAudio.Mqtt;
using Xunit;

namespace MultiRoomAudio.Tests.Mqtt;

public class MqttStatePayloadsTests
{
    private static PlayerResponse SamplePlayer() => new(
        Name: "Living Room",
        State: PlayerState.Playing,
        Device: "dac0",
        ClientId: "abc123",
        ServerUrl: "http://ma",
        ServerName: "Music Assistant",
        ConnectedAddress: "192.168.1.50:8095",
        Volume: 50,
        StartupVolume: 40,
        IsMuted: false,
        DelayMs: 0,
        OutputLatencyMs: 10,
        CreatedAt: DateTime.UnixEpoch,
        ConnectedAt: DateTime.UnixEpoch,
        ErrorMessage: null,
        IsClockSynced: true,
        Metrics: null,
        IsPendingReconnection: false,
        ReconnectionAttempts: 0);

    [Fact]
    public void Player_SerializesExpectedFields()
    {
        using var doc = JsonDocument.Parse(MqttStatePayloads.Player(SamplePlayer()));
        var root = doc.RootElement;

        Assert.Equal("playing", root.GetProperty("state").GetString());
        Assert.Equal("Music Assistant", root.GetProperty("server_name").GetString());
        Assert.Equal("192.168.1.50:8095", root.GetProperty("server_address").GetString());
        Assert.Equal("ON", root.GetProperty("clock_synced").GetString());
        Assert.Equal("OFF", root.GetProperty("reconnect_pending").GetString());
        Assert.Equal(0, root.GetProperty("reconnect_attempts").GetInt32());
    }

    [Fact]
    public void Container_SerializesHealth()
    {
        using var doc = JsonDocument.Parse(
            MqttStatePayloads.Container(ready: true, version: "1.2.3", playerCount: 4,
                audioBackend: "pulse", environment: "haos"));
        var root = doc.RootElement;

        Assert.Equal("ON", root.GetProperty("ready").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal(4, root.GetProperty("player_count").GetInt32());
    }
}
