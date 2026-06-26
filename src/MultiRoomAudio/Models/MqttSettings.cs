namespace MultiRoomAudio.Models;

/// <summary>
/// Persisted MQTT bridge configuration (mqtt.yaml). Environment variables and
/// HAOS options override these values at load time.
/// </summary>
public class MqttSettings
{
    /// <summary>Whether the MQTT bridge is enabled. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Broker hostname or IP. Null when unset.</summary>
    public string? Host { get; set; }

    /// <summary>Broker port. 1883 plain, 8883 TLS.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>Broker username, or null for anonymous.</summary>
    public string? Username { get; set; }

    /// <summary>Broker password, or null for anonymous.</summary>
    public string? Password { get; set; }

    /// <summary>Whether to connect over TLS.</summary>
    public bool UseTls { get; set; }

    /// <summary>Home Assistant MQTT Discovery prefix.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>Root topic for this bridge's state/command topics.</summary>
    public string BaseTopic { get; set; } = "multiroom-audio";
}

/// <summary>
/// MQTT settings returned by the API. Never includes the password itself.
/// </summary>
public record MqttSettingsResponse(
    bool Enabled,
    string? Host,
    int Port,
    string? Username,
    bool HasPassword,
    bool UseTls,
    string DiscoveryPrefix,
    string BaseTopic,
    bool Connected,
    string? LastError,
    string Source);

/// <summary>
/// Partial update to MQTT settings. Only non-null fields are applied.
/// A null Password leaves the stored password unchanged; an empty string clears it.
/// </summary>
public record MqttSettingsUpdateRequest(
    bool? Enabled,
    string? Host,
    int? Port,
    string? Username,
    string? Password,
    bool? UseTls,
    string? DiscoveryPrefix,
    string? BaseTopic);
