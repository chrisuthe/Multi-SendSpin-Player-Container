using System.Globalization;

namespace MultiRoomAudio.Mqtt;

public enum MqttCommandKind { Unknown, PlayerOffset, PlayerRestart }

public record ParsedCommand(MqttCommandKind Kind, string PlayerClientId, int? IntValue);

/// <summary>
/// Parses inbound MQTT command topics into typed commands. Pure — no dispatch.
/// Topic shape: {base}/player/{sanitizedClientId}/{command}/set
/// </summary>
public static class MqttCommand
{
    public static ParsedCommand Parse(string baseTopic, string topic, string payload)
    {
        var prefix = baseTopic.TrimEnd('/') + "/player/";
        if (!topic.StartsWith(prefix, StringComparison.Ordinal))
            return new ParsedCommand(MqttCommandKind.Unknown, "", null);

        var rest = topic[prefix.Length..];           // {id}/{command}/set
        var parts = rest.Split('/');
        if (parts.Length != 3 || parts[2] != "set")
            return new ParsedCommand(MqttCommandKind.Unknown, "", null);

        var id = parts[0];
        return parts[1] switch
        {
            "offset" => new ParsedCommand(MqttCommandKind.PlayerOffset, id,
                int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null),
            "restart" => new ParsedCommand(MqttCommandKind.PlayerRestart, id, null),
            _ => new ParsedCommand(MqttCommandKind.Unknown, id, null),
        };
    }
}
