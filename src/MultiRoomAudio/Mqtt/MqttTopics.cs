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

    public string BridgeAvailabilityTopic => $"{_base}/bridge/availability";

    public string ContainerStateTopic => $"{_base}/bridge/state";

    public string PlayerStateTopic(string clientId) => $"{_base}/player/{Sanitize(clientId)}/state";

    public string PlayerCommandTopic(string clientId, string command) =>
        $"{_base}/player/{Sanitize(clientId)}/{command}/set";

    /// <summary>Single wildcard subscription covering all player command topics.</summary>
    public string PlayerCommandSubscription => $"{_base}/player/+/+/set";

    public string DiscoveryTopic(string component, string objectId) =>
        $"{_prefix}/{component}/{objectId}/config";

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
