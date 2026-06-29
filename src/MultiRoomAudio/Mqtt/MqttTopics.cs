using System.Text;

namespace MultiRoomAudio.Mqtt;

/// <summary>
/// Builds MQTT topic strings and Home Assistant discovery topics.
/// Pure string construction — no I/O.
/// </summary>
public class MqttTopics
{
    private readonly string _base;
    private readonly string _prefix;

    public MqttTopics(string baseTopic, string discoveryPrefix)
    {
        _base = baseTopic.TrimEnd('/');
        _prefix = discoveryPrefix.TrimEnd('/');
    }

    /// <summary>Retained topic published as "online"/"offline" to indicate bridge availability.</summary>
    public string BridgeAvailabilityTopic => $"{_base}/bridge/availability";

    /// <summary>Retained topic carrying the serialized container state payload.</summary>
    public string ContainerStateTopic => $"{_base}/bridge/state";

    /// <summary>Retained topic carrying the serialized state payload for the given player client ID.</summary>
    public string PlayerStateTopic(string clientId) => $"{_base}/player/{Sanitize(clientId)}/state";

    /// <summary>Topic on which Home Assistant publishes commands for the given player client ID and command name.</summary>
    public string PlayerCommandTopic(string clientId, string command) =>
        $"{_base}/player/{Sanitize(clientId)}/{command}/set";

    /// <summary>Single wildcard subscription covering all player command topics.</summary>
    public string PlayerCommandSubscription => $"{_base}/player/+/+/set";

    private string AmpZone(string boardId, int channel) => $"{Sanitize(boardId)}_{channel}";

    /// <summary>State topic for an amp/zone (one JSON document per zone).</summary>
    public string AmpStateTopic(string boardId, int channel) => $"{_base}/amp/{AmpZone(boardId, channel)}/state";

    /// <summary>Command topic for an amp/zone control (e.g. "override").</summary>
    public string AmpCommandTopic(string boardId, int channel, string command) =>
        $"{_base}/amp/{AmpZone(boardId, channel)}/{command}/set";

    /// <summary>Single wildcard subscription covering all amp command topics.</summary>
    public string AmpCommandSubscription => $"{_base}/amp/+/+/set";

    /// <summary>Home Assistant MQTT discovery config topic for the given component type and object ID.</summary>
    public string DiscoveryTopic(string component, string objectId) =>
        $"{_prefix}/{component}/{objectId}/config";

    public string DeviceDiscoveryTopic(string deviceId) =>
        $"{_prefix}/device/{deviceId}/config";

    /// <summary>Lowercase and replace any character outside [a-z0-9_-] with '_'. Collapse consecutive underscores.</summary>
    public static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var lastWasUnderscore = false;
        foreach (var c in raw.ToLowerInvariant())
        {
            var isValid = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            var nextChar = isValid ? c : '_';

            if (nextChar == '_' && lastWasUnderscore)
                continue; // Skip consecutive underscores

            sb.Append(nextChar);
            lastWasUnderscore = (nextChar == '_');
        }
        return sb.ToString();
    }
}
