using System.Globalization;

namespace MultiRoomAudio.Mqtt;

public enum MqttCommandKind { Unknown, PlayerOffset, PlayerRestart, AmpOverride }

public record ParsedCommand(
    MqttCommandKind Kind,
    string PlayerClientId,
    int? IntValue,
    string? AmpZone = null,
    bool? BoolValue = null);

/// <summary>
/// Parses inbound MQTT command topics into typed commands. Pure — no dispatch.
/// Player: {base}/player/{sanitizedClientId}/{command}/set
/// Amp:    {base}/amp/{sanitizedBoardId_channel}/{command}/set
/// </summary>
public static class MqttCommand
{
    public static ParsedCommand Parse(string baseTopic, string topic, string payload)
    {
        var root = baseTopic.TrimEnd('/');

        var playerPrefix = root + "/player/";
        if (topic.StartsWith(playerPrefix, StringComparison.Ordinal))
        {
            var parts = topic[playerPrefix.Length..].Split('/');   // {id}/{command}/set
            if (parts.Length != 3 || parts[2] != "set")
                return Unknown();
            var id = parts[0];
            return parts[1] switch
            {
                "offset" => new ParsedCommand(MqttCommandKind.PlayerOffset, id,
                    int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null),
                "restart" => new ParsedCommand(MqttCommandKind.PlayerRestart, id, null),
                _ => new ParsedCommand(MqttCommandKind.Unknown, id, null),
            };
        }

        var ampPrefix = root + "/amp/";
        if (topic.StartsWith(ampPrefix, StringComparison.Ordinal))
        {
            var parts = topic[ampPrefix.Length..].Split('/');      // {zone}/{command}/set
            if (parts.Length != 3 || parts[2] != "set")
                return Unknown();
            var zone = parts[0];
            return parts[1] switch
            {
                "override" => new ParsedCommand(MqttCommandKind.AmpOverride, "", null,
                    AmpZone: zone, BoolValue: payload.Trim().Equals("ON", StringComparison.OrdinalIgnoreCase)),
                _ => Unknown(),
            };
        }

        return Unknown();
    }

    private static ParsedCommand Unknown() => new(MqttCommandKind.Unknown, "", null);
}
