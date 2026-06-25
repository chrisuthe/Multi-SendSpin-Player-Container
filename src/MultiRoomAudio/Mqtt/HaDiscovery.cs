using System.Text.Json;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Mqtt;

/// <summary>
/// Builds Home Assistant MQTT Discovery config messages for the bridge's devices.
/// All payloads are retained config JSON; state comes from the shared per-device
/// state topic referenced by each entity's value_template.
/// </summary>
public class HaDiscovery
{
    private readonly MqttTopics _topics;
    private readonly string _version;

    public HaDiscovery(MqttTopics topics, string bridgeVersion)
    {
        _topics = topics;
        _version = bridgeVersion;
    }

    public record DiscoveryMessage(string Topic, string Payload);

    public IReadOnlyList<DiscoveryMessage> ForPlayer(PlayerResponse p)
    {
        var id = MqttTopics.Sanitize(p.ClientId);
        var stateTopic = _topics.PlayerStateTopic(p.ClientId);
        var deviceId = $"mra_player_{id}";
        var device = Device(deviceId, p.Name, "Audio Player");

        DiscoveryMessage Entity(string component, string key, string name,
            Action<Utf8JsonWriter> extra)
            => Build(component, $"mra_{id}_{key}", name, deviceId, stateTopic, device, extra);

        return new List<DiscoveryMessage>
        {
            Entity("sensor", "state", $"{p.Name} State", w =>
                w.WriteString("value_template", "{{ value_json.state }}")),
            Entity("sensor", "server", $"{p.Name} Server", w =>
            {
                w.WriteString("value_template", "{{ value_json.server_name }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("binary_sensor", "clock_synced", $"{p.Name} Clock Synced", w =>
            {
                w.WriteString("value_template", "{{ value_json.clock_synced }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("binary_sensor", "reconnect_pending", $"{p.Name} Reconnecting", w =>
            {
                w.WriteString("value_template", "{{ value_json.reconnect_pending }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("sensor", "reconnect_attempts", $"{p.Name} Reconnect Attempts", w =>
            {
                w.WriteString("value_template", "{{ value_json.reconnect_attempts }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("number", "offset", $"{p.Name} Delay Offset", w =>
            {
                w.WriteString("command_topic", _topics.PlayerCommandTopic(p.ClientId, "offset"));
                w.WriteString("value_template", "{{ value_json.delay_ms }}");
                w.WriteNumber("min", -5000);
                w.WriteNumber("max", 5000);
                w.WriteString("unit_of_measurement", "ms");
                w.WriteString("mode", "box");
                w.WriteString("entity_category", "config");
            }),
            Entity("button", "restart", $"{p.Name} Restart", w =>
            {
                w.WriteString("command_topic", _topics.PlayerCommandTopic(p.ClientId, "restart"));
                w.WriteString("payload_press", "PRESS");
                w.WriteString("entity_category", "config");
            }),
        };
    }

    public IReadOnlyList<DiscoveryMessage> ForContainer(string instanceId)
    {
        var id = MqttTopics.Sanitize(instanceId);
        var stateTopic = _topics.ContainerStateTopic;
        var deviceId = $"mra_bridge_{id}";
        var device = Device(deviceId, "Multi-Room Audio", "Controller");

        DiscoveryMessage Entity(string component, string key, string name,
            Action<Utf8JsonWriter> extra)
            => Build(component, $"mra_bridge_{id}_{key}", name, deviceId, stateTopic, device, extra);

        return new List<DiscoveryMessage>
        {
            Entity("binary_sensor", "ready", "ready", w =>
            {
                w.WriteString("value_template", "{{ value_json.ready }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("device_class", "running");
            }),
            Entity("sensor", "version", "version", w =>
            {
                w.WriteString("value_template", "{{ value_json.version }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("sensor", "player_count", "player_count", w =>
                w.WriteString("value_template", "{{ value_json.player_count }}")),
            Entity("sensor", "audio_backend", "audio_backend", w =>
            {
                w.WriteString("value_template", "{{ value_json.audio_backend }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("sensor", "environment", "environment", w =>
            {
                w.WriteString("value_template", "{{ value_json.environment }}");
                w.WriteString("entity_category", "diagnostic");
            }),
        };
    }

    private DiscoveryMessage Build(string component, string uniqueId, string name,
        string deviceId, string stateTopic, Action<Utf8JsonWriter> deviceBlock,
        Action<Utf8JsonWriter> extra)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("unique_id", uniqueId);
            w.WriteString("object_id", uniqueId);
            w.WriteString("state_topic", stateTopic);
            w.WriteString("availability_topic", _topics.BridgeAvailabilityTopic);
            w.WriteString("payload_available", "online");
            w.WriteString("payload_not_available", "offline");
            extra(w);
            w.WritePropertyName("device");
            deviceBlock(w);
            w.WriteEndObject();
        }
        var payload = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        return new DiscoveryMessage(_topics.DiscoveryTopic(component, uniqueId), payload);
    }

    private Action<Utf8JsonWriter> Device(string identifier, string name, string model) => w =>
    {
        w.WriteStartObject();
        w.WritePropertyName("identifiers");
        w.WriteStartArray();
        w.WriteStringValue(identifier);
        w.WriteEndArray();
        w.WriteString("name", name);
        w.WriteString("manufacturer", "Multi-Room Audio");
        w.WriteString("model", model);
        w.WriteString("sw_version", _version);
        w.WriteEndObject();
    };
}
