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

    private DiscoveryMessage BuildRemovalMessage(string component, string deviceId)
    {
        return new DiscoveryMessage(_topics.DiscoveryTopic(component, deviceId), "");
    }

    /// <summary>
    /// Returns HA MQTT discovery config messages for all entities belonging to the container-level bridge device.
    /// </summary>
    public IReadOnlyList<DiscoveryMessage> ForPlayerDevice(PlayerResponse p)
    {
        var id = MqttTopics.Sanitize(p.ClientId);
        var stateTopic = _topics.PlayerStateTopic(p.ClientId);
        var deviceId = $"mra_player_{id}";
        var label = $"{p.Name}";

        using var stream = new MemoryStream();
        // Build Device Messge
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WritePropertyName("dev");
            w.WriteStartObject();
            w.WriteString("ids", deviceId); // Identifiers
            w.WriteString("name", "Multi-Room Audio");
            w.WriteString("mf", "Multi-Room Audio");// Manufacturer
            w.WriteString("model", "Audio Player");
            w.WriteString("sw", _version); //sw_version
            w.WriteEndObject();

            w.WritePropertyName("o"); // origin
            w.WriteStartObject();
            w.WriteString("name", "Multiroom Audio Sendspin Container");
            w.WriteString("sw", _version); // software Version
            w.WriteEndObject();

            // Start Components
            w.WritePropertyName("components"); // components
            w.WriteStartObject();

            // Ready Sensor
            w.WritePropertyName($"{deviceId}_State");
            w.WriteStartObject();
            w.WriteString("name", $"{p.Name} State");
            w.WriteString("unique_id", $"{deviceId}_State");
            w.WriteString("p", "sensor");
            w.WriteString("value_template", "{{ value_json.state }}");
            w.WriteEndObject();

            // Player Server Sensor
            w.WritePropertyName($"{deviceId}_server");
            w.WriteStartObject();
            w.WriteString("name", $"{p.Name} Server");
            w.WriteString("unique_id", $"{deviceId}_server");
            w.WriteString("p", "sensor");
            w.WriteString("value_template", "{{ value_json.server_name }}");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            // Clock Synced Sensor
            w.WritePropertyName($"{deviceId}_clock_synced");
            w.WriteStartObject();
            w.WriteString("name", $"{p.Name} Clocke Synced");
            w.WriteString("unique_id", $"{deviceId}_clock_synced");
            w.WriteString("p", "binary_sensor");
            w.WriteString("value_template", "{{ value_json.clock_synced }}");
            w.WriteString("payload_on", "ON");
            w.WriteString("payload_off", "OFF");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            // Reconnect Pending Sensor
            w.WritePropertyName($"{deviceId}_reconnect_pending");
            w.WriteStartObject();
            w.WriteString("name", $"{p.Name} Reconnecting");
            w.WriteString("unique_id", $"{deviceId}_reconnect_pending");
            w.WriteString("p", "binary_sensor");
            w.WriteString("value_template", "{{ value_json.reconnect_pending }}");
            w.WriteString("payload_on", "ON");
            w.WriteString("payload_off", "OFF");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            // Reconnect Attempts Sensor
            w.WritePropertyName($"{deviceId}_reconnect_attempts");
            w.WriteStartObject();
            w.WriteString("name", $"{p.Name} Reconnect Attempts");
            w.WriteString("unique_id", $"{deviceId}_reconnect_attempts");
            w.WriteString("p", "sensor");
            w.WriteString("value_template", "{{ value_json.reconnect_attempts }}");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            // Ready Sensor
            w.WritePropertyName($"{deviceId}_offset");
            w.WriteStartObject();
            w.WriteString("name", $"{p.Name} Delay Offset");
            w.WriteString("unique_id", $"{deviceId}_offset");
            w.WriteString("p", "number");
            w.WriteString("command_topic", _topics.PlayerCommandTopic(p.ClientId, "offset"));
            w.WriteString("value_template", "{{ value_json.delay_ms }}");
            w.WriteNumber("min", -5000);
            w.WriteNumber("max", 5000);
            w.WriteString("unit_of_measurement", "ms");
            w.WriteString("mode", "box");
            w.WriteString("entity_category", "config");
            w.WriteEndObject();

            // Restart Sensor
            w.WritePropertyName($"{deviceId}_restart");
            w.WriteStartObject();
            w.WriteString("name", $"{p.Name} Restart");
            w.WriteString("unique_id", $"{deviceId}_restart");
            w.WriteString("p", "button");
            w.WriteString("command_topic", _topics.PlayerCommandTopic(p.ClientId, "restart"));
            w.WriteString("payload_press", "PRESS");
            w.WriteString("entity_category", "config");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            //End Components
            w.WriteEndObject();

            w.WriteString("state_topic", stateTopic);
            w.WriteString("availability_topic", _topics.BridgeAvailabilityTopic);
            w.WriteString("payload_available", "online");
            w.WriteString("payload_not_available", "offline");
            w.WriteEndObject();
        }
        var payload = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        DiscoveryMessage Entity(string component, string key)
            => BuildRemovalMessage(component, $"mra_{id}_{key}");

        return new List<DiscoveryMessage>
        {
            new DiscoveryMessage(_topics.DeviceDiscoveryTopic(deviceId), payload),
            Entity("sensor", "state"),
            Entity("sensor", "server"),
            Entity("binary_sensor", "clock_synced"),
            Entity("binary_sensor", "reconnect_pending"),
            Entity("sensor", "reconnect_attempts"),
            Entity("number", "offset"),
            Entity("button", "restart"),
        };
    }

    /// <summary>
    /// Returns HA MQTT discovery config messages for all entities belonging to the container-level bridge device.
    /// </summary>
    public IReadOnlyList<DiscoveryMessage> ForContainerDevice(string instanceId)
    {
        var id = MqttTopics.Sanitize(instanceId);
        var stateTopic = _topics.ContainerStateTopic;
        var deviceId = $"mra_bridge_{id}";
        var label = $"MRA Bridge {id}";

        using var stream = new MemoryStream();
        // Build Device Messge
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WritePropertyName("dev");
            w.WriteStartObject();
            w.WriteString("ids", deviceId); // Identifiers
            w.WriteString("name", "Multi-Room Audio");
            w.WriteString("mf", "Multi-Room Audio");// Manufacturer
            w.WriteString("model", "Controller");
            w.WriteString("sw", _version); //sw_version
            w.WriteEndObject();

            w.WritePropertyName("o"); // origin
            w.WriteStartObject();
            w.WriteString("name", "Multiroom Audio Sendspin Container");
            w.WriteString("sw", _version); // software Version
            w.WriteEndObject();

            // Start Components
            w.WritePropertyName("components"); // components
            w.WriteStartObject();

            // Ready Sensor
            w.WritePropertyName($"{deviceId}_ready");
            w.WriteStartObject();
            w.WriteString("name", $"{label} Ready");
            w.WriteString("unique_id", $"{deviceId}_ready");
            w.WriteString("p", "binary_sensor");
            w.WriteString("value_template", "{{ value_json.ready }}");
            w.WriteString("payload_on", "ON");
            w.WriteString("payload_off", "OFF");
            w.WriteString("device_class", "running");
            w.WriteEndObject();

            // Version Sensor
            w.WritePropertyName($"{deviceId}_version");
            w.WriteStartObject();
            w.WriteString("name", $"{label} Version");
            w.WriteString("unique_id", $"{deviceId}_version");
            w.WriteString("p", "sensor");
            w.WriteString("value_template", "{{ value_json.version }}");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            // player Count sensor
            w.WritePropertyName($"{deviceId}_player_count");
            w.WriteStartObject();
            w.WriteString("name", $"{label} Players");
            w.WriteString("unique_id", $"{deviceId}_player_count");
            w.WriteString("p", "sensor");
            w.WriteString("value_template", "{{ value_json.player_count }}");
            w.WriteEndObject();

            // Board Connected Sensor
            w.WritePropertyName($"{label}_audio_backend");
            w.WriteStartObject();
            w.WriteString("name", $"{label} Audio Backend");
            w.WriteString("unique_id", $"{label}_audio_backend");
            w.WriteString("p", "sensor");
            w.WriteString("value_template", "{{ value_json.audio_backend }}");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            // Environment sensor
            w.WritePropertyName($"{deviceId}_environment");
            w.WriteStartObject();
            w.WriteString("name", $"{label} Environment");
            w.WriteString("unique_id", $"{deviceId}_environment");
            w.WriteString("p", "sensor");
            w.WriteString("value_template", "{{ value_json.environment }}");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            //End Components
            w.WriteEndObject();

            w.WriteString("state_topic", stateTopic);
            w.WriteString("availability_topic", _topics.BridgeAvailabilityTopic);
            w.WriteString("payload_available", "online");
            w.WriteString("payload_not_available", "offline");
            w.WriteEndObject();
        }
        var payload = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        DiscoveryMessage Entity(string component, string key)
            => BuildRemovalMessage(component, $"mra_bridge_{id}_{key}");

        return new List<DiscoveryMessage>
        {
            new DiscoveryMessage(_topics.DeviceDiscoveryTopic(deviceId), payload),

            Entity("binary_sensor", "ready"),
            Entity("sensor", "version"),
            Entity("sensor", "player_count"),
            Entity("sensor", "audio_backend"),
            Entity("sensor", "environment"),
        };
    }

    /// <summary>
    /// Returns HA MQTT discovery config messages for all entities belonging to a single amplifier zone (relay channel).
    /// </summary>
    public IReadOnlyList<DiscoveryMessage> ForAmpDevice(string boardId, string? boardDisplayName, TriggerResponse t)
    {

        var zone = $"{MqttTopics.Sanitize(boardId)}_{t.Channel}";
        var stateTopic = _topics.AmpStateTopic(boardId, t.Channel);
        var deviceId = $"mra_amp_{zone}";
        var label = !string.IsNullOrWhiteSpace(t.ZoneName) ? t.ZoneName
                  : !string.IsNullOrWhiteSpace(boardDisplayName) ? $"{boardDisplayName} CH{t.Channel}"
                  : $"{boardId} CH{t.Channel}";

        using var stream = new MemoryStream();
        // Build Device Messge
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WritePropertyName("dev");
            w.WriteStartObject();
            w.WriteString("ids", $"mra_amp_{zone}"); // Identifiers
            w.WriteString("name", label!);
            w.WriteString("mf", "Multi-Room Audio");// Manufacturer
            w.WriteString("model", "Amplifier Zone");
            w.WriteString("sw", _version); //sw_version
            w.WriteEndObject();

            w.WritePropertyName("o"); // origin
            w.WriteStartObject();
            w.WriteString("name", "Multiroom Audio Sendspin Container");
            w.WriteString("sw", _version); // software Version
            w.WriteEndObject();

            // Start Components
            w.WritePropertyName("components"); // components
            w.WriteStartObject();
            // Poser Sensor
            w.WritePropertyName($"{deviceId}_power"); // start of power binary sensor
            w.WriteStartObject();
            w.WriteString("name", $"{label} Power");
            w.WriteString("unique_id", $"{deviceId}_power");
            w.WriteString("p", "binary_sensor");
            w.WriteString("value_template", "{{ value_json.power }}");
            w.WriteString("payload_on", "ON");
            w.WriteString("payload_off", "OFF");
            w.WriteString("device_class", "power");
            w.WriteEndObject();

            // Scheduled Off Sensor
            w.WritePropertyName($"{deviceId}_scheduled_off"); // start of Scheduled Off sensor
            w.WriteStartObject();
            w.WriteString("name", $"{label} Scheduled Off");
            w.WriteString("unique_id", $"{deviceId}_scheduled_off");
            w.WriteString("p", "sensor");
            w.WriteString("value_template", "{{ value_json.scheduled_off }}");
            w.WriteString("device_class", "timestamp");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            // Override Switch
            w.WritePropertyName($"{deviceId}_override"); // start of Override Switch
            w.WriteStartObject();
            w.WriteString("name", $"{label} Override");
            w.WriteString("unique_id", $"{deviceId}_override");
            w.WriteString("p", "switch");
            w.WriteString("command_topic", _topics.AmpCommandTopic(boardId, t.Channel, "override"));
            w.WriteString("value_template", "{{ value_json.override }}");
            w.WriteString("state_on", "ON");
            w.WriteString("state_off", "OFF");
            w.WriteString("payload_on", "ON");
            w.WriteString("payload_off", "OFF");
            w.WriteEndObject();

            // Board Connected Sensor
            w.WritePropertyName($"{deviceId}_board_connected"); // start of Board Connected Binary Sensor
            w.WriteStartObject();
            w.WriteString("name", $"{label} Board Connected");
            w.WriteString("unique_id", $"{deviceId}_board_connected");
            w.WriteString("p", "binary_sensor");
            w.WriteString("value_template", "{{ value_json.board_connected }}");
            w.WriteString("payload_on", "ON");
            w.WriteString("payload_off", "OFF");
            w.WriteString("device_class", "connectivity");
            w.WriteString("entity_category", "diagnostic");
            w.WriteString("enabled_by_default", "false");
            w.WriteEndObject();

            //End Components
            w.WriteEndObject();

            w.WriteString("state_topic", stateTopic);
            w.WriteString("availability_topic", _topics.BridgeAvailabilityTopic);
            w.WriteString("payload_available", "online");
            w.WriteString("payload_not_available", "offline");
            w.WriteEndObject();
        }
        var payload = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        DiscoveryMessage Entity(string component, string key)
            => BuildRemovalMessage(component, $"mra_amp_{zone}_{key}");
        return new List<DiscoveryMessage>
        {
            new DiscoveryMessage(_topics.DeviceDiscoveryTopic(deviceId), payload),
            Entity("binary_sensor", "power"),
            Entity("sensor", "scheduled_off"),
            Entity("switch", "override"),
            Entity("binary_sensor", "board_connected")
        };
    }

    private Action<Utf8JsonWriter> Device(string identifier, string name, string model) => w =>
    {
        w.WriteStartObject();
        //w.WritePropertyName("dev");        
        w.WriteString("ids", identifier); // Identifiers
        w.WriteString("name", name);
        w.WriteString("mf", "Multi-Room Audio");// Manufacturer
        w.WriteString("model", model);
        w.WriteString("sw", _version); //sw_version
        w.WriteEndObject();
        //w.WriteStartArray();
        //w.WriteStringValue(identifier);
        //w.WriteEndArray();
    };
}
