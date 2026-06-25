using System.Text.Json;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Mqtt;

/// <summary>
/// Builds the JSON state payloads published to MQTT. Each device publishes a
/// single JSON document; entity discovery configs reference fields via value_template.
/// </summary>
public static class MqttStatePayloads
{
    private static string OnOff(bool on) => on ? "ON" : "OFF";

    /// <summary>Per-player state document.</summary>
    public static string Player(PlayerResponse p) => JsonSerializer.Serialize(new
    {
        state = p.State.ToString().ToLowerInvariant(),
        server_name = p.ServerName,
        server_address = p.ConnectedAddress,
        clock_synced = OnOff(p.IsClockSynced),
        reconnect_pending = OnOff(p.IsPendingReconnection),
        reconnect_attempts = p.ReconnectionAttempts ?? 0,
        delay_ms = p.DelayMs,
    });

    /// <summary>Container/hub health document.</summary>
    public static string Container(bool ready, string version, int playerCount,
        string audioBackend, string environment) => JsonSerializer.Serialize(new
    {
        ready = OnOff(ready),
        version,
        player_count = playerCount,
        audio_backend = audioBackend,
        environment,
    });
}
