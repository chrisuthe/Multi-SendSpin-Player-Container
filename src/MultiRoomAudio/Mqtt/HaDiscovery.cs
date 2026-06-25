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

    /// <summary>
    /// Returns HA MQTT discovery config messages for all entities belonging to a single audio player device.
    /// </summary>
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

    /// <summary>
    /// Returns HA MQTT discovery config messages for all entities belonging to the container-level bridge device.
    /// </summary>
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
            Entity("binary_sensor", "ready", "Multi-Room Audio Ready", w =>
            {
                w.WriteString("value_template", "{{ value_json.ready }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("device_class", "running");
            }),
            Entity("sensor", "version", "Multi-Room Audio Version", w =>
            {
                w.WriteString("value_template", "{{ value_json.version }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("sensor", "player_count", "Multi-Room Audio Players", w =>
                w.WriteString("value_template", "{{ value_json.player_count }}")),
            Entity("sensor", "audio_backend", "Multi-Room Audio Backend", w =>
            {
                w.WriteString("value_template", "{{ value_json.audio_backend }}");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("sensor", "environment", "Multi-Room Audio Environment", w =>
            {
                w.WriteString("value_template", "{{ value_json.environment }}");
                w.WriteString("entity_category", "diagnostic");
            }),
        };
    }

    /// <summary>
    /// Returns HA MQTT discovery config messages for all entities belonging to a single amplifier zone (relay channel).
    /// </summary>
    public IReadOnlyList<DiscoveryMessage> ForAmp(string boardId, string? boardDisplayName, TriggerResponse t)
    {
        var zone = $"{MqttTopics.Sanitize(boardId)}_{t.Channel}";
        var stateTopic = _topics.AmpStateTopic(boardId, t.Channel);
        var deviceId = $"mra_amp_{zone}";
        var label = !string.IsNullOrWhiteSpace(t.ZoneName) ? t.ZoneName
                  : !string.IsNullOrWhiteSpace(boardDisplayName) ? $"{boardDisplayName} CH{t.Channel}"
                  : $"{boardId} CH{t.Channel}";
        var device = Device(deviceId, label!, "Amplifier Zone");

        DiscoveryMessage Entity(string component, string key, string name, Action<Utf8JsonWriter> extra)
            => Build(component, $"mra_amp_{zone}_{key}", name, deviceId, stateTopic, device, extra);

        return new List<DiscoveryMessage>
        {
            Entity("binary_sensor", "power", $"{label} Power", w =>
            {
                w.WriteString("value_template", "{{ value_json.power }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("device_class", "power");
            }),
            Entity("sensor", "scheduled_off", $"{label} Scheduled Off", w =>
            {
                w.WriteString("value_template", "{{ value_json.scheduled_off }}");
                w.WriteString("device_class", "timestamp");
                w.WriteString("entity_category", "diagnostic");
            }),
            Entity("switch", "override", $"{label} Override", w =>
            {
                w.WriteString("command_topic", _topics.AmpCommandTopic(boardId, t.Channel, "override"));
                w.WriteString("value_template", "{{ value_json.override }}");
                w.WriteString("state_on", "ON");
                w.WriteString("state_off", "OFF");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
            }),
            Entity("binary_sensor", "board_connected", $"{label} Board Connected", w =>
            {
                w.WriteString("value_template", "{{ value_json.board_connected }}");
                w.WriteString("payload_on", "ON");
                w.WriteString("payload_off", "OFF");
                w.WriteString("device_class", "connectivity");
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
