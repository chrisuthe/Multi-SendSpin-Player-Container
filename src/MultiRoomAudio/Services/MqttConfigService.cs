using MultiRoomAudio.Models;

namespace MultiRoomAudio.Services;

/// <summary>
/// Loads and persists MQTT bridge settings (mqtt.yaml), applying environment
/// variable and HAOS option overrides with standard precedence:
/// env var → HAOS option → yaml → default.
/// </summary>
public class MqttConfigService : YamlFileService<MqttSettings>
{
    private readonly EnvironmentService _env;
    private MqttSettings _current = new();

    /// <summary>Initializes the service with the environment-resolved config path and logger.</summary>
    public MqttConfigService(EnvironmentService env, ILogger<MqttConfigService> logger)
        : base(env.MqttConfigPath, logger)
    {
        _env = env;
    }

    /// <summary>Effective settings after overrides. Call <see cref="Reload"/> to refresh.</summary>
    public MqttSettings Current => _current;

    /// <summary>Highest-priority source that supplied the broker host.</summary>
    public string Source { get; private set; } = "default";

    /// <summary>Load yaml and apply env/HAOS overrides.</summary>
    public void Reload()
    {
        Load();
        var env = new Dictionary<string, string?>
        {
            ["MQTT_ENABLED"] = Environment.GetEnvironmentVariable("MQTT_ENABLED"),
            ["MQTT_HOST"] = Environment.GetEnvironmentVariable("MQTT_HOST"),
            ["MQTT_PORT"] = Environment.GetEnvironmentVariable("MQTT_PORT"),
            ["MQTT_USERNAME"] = Environment.GetEnvironmentVariable("MQTT_USERNAME"),
            ["MQTT_PASSWORD"] = Environment.GetEnvironmentVariable("MQTT_PASSWORD"),
            ["MQTT_TLS"] = Environment.GetEnvironmentVariable("MQTT_TLS"),
            ["MQTT_DISCOVERY_PREFIX"] = Environment.GetEnvironmentVariable("MQTT_DISCOVERY_PREFIX"),
            ["MQTT_BASE_TOPIC"] = Environment.GetEnvironmentVariable("MQTT_BASE_TOPIC"),
        };

        _current = ApplyOverrides(
            Data,
            env,
            key => _env.IsHaos ? _env.GetHaosOption<bool?>(key) : null,
            key => _env.IsHaos ? _env.GetHaosOption<string?>(key) : null,
            key => _env.IsHaos ? _env.GetHaosOption<int?>(key) : null);

        Source = ResolveSource(env);
    }

    /// <summary>Apply a partial update and persist to yaml, then reload effective settings.</summary>
    public void Update(MqttSettingsUpdateRequest request)
    {
        Lock.EnterWriteLock();
        try
        {
            if (request.Enabled is { } enabled) Data.Enabled = enabled;
            if (request.Host is not null) Data.Host = string.IsNullOrWhiteSpace(request.Host) ? null : request.Host;
            if (request.Port is { } port) Data.Port = port;
            if (request.Username is not null) Data.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username;
            if (request.Password is not null) Data.Password = request.Password.Length == 0 ? null : request.Password;
            if (request.UseTls is { } tls) Data.UseTls = tls;
            if (!string.IsNullOrWhiteSpace(request.DiscoveryPrefix)) Data.DiscoveryPrefix = request.DiscoveryPrefix;
            if (!string.IsNullOrWhiteSpace(request.BaseTopic)) Data.BaseTopic = request.BaseTopic;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
        Save();
        Reload();
    }

    private string ResolveSource(IReadOnlyDictionary<string, string?> env)
    {
        if (!string.IsNullOrWhiteSpace(env["MQTT_HOST"])) return "env";
        if (_env.IsHaos && !string.IsNullOrWhiteSpace(_env.GetHaosOption<string?>("MQTT_HOST"))) return "haos";
        if (!string.IsNullOrWhiteSpace(Data.Host)) return "yaml";
        return "default";
    }

    /// <summary>
    /// Pure override application: env var wins, then HAOS option, then the yaml value.
    /// </summary>
    public static MqttSettings ApplyOverrides(
        MqttSettings fromYaml,
        IReadOnlyDictionary<string, string?> env,
        Func<string, bool?> haosBool,
        Func<string, string?> haosString,
        Func<string, int?> haosInt)
    {
        static bool IsTruthy(string? v) =>
            v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ||
                          v.Equals("yes", StringComparison.OrdinalIgnoreCase));

        string? EnvStr(string k) => string.IsNullOrWhiteSpace(env.GetValueOrDefault(k)) ? null : env[k];

        return new MqttSettings
        {
            Enabled = EnvStr("MQTT_ENABLED") is { } en ? IsTruthy(en)
                      : haosBool("MQTT_ENABLED") ?? fromYaml.Enabled,
            Host = EnvStr("MQTT_HOST") ?? haosString("MQTT_HOST") ?? fromYaml.Host,
            Port = (EnvStr("MQTT_PORT") is { } p && int.TryParse(p, out var pv)) ? pv
                   : haosInt("MQTT_PORT") ?? fromYaml.Port,
            Username = EnvStr("MQTT_USERNAME") ?? haosString("MQTT_USERNAME") ?? fromYaml.Username,
            Password = EnvStr("MQTT_PASSWORD") ?? haosString("MQTT_PASSWORD") ?? fromYaml.Password,
            UseTls = EnvStr("MQTT_TLS") is { } tls ? IsTruthy(tls)
                     : haosBool("MQTT_TLS") ?? fromYaml.UseTls,
            DiscoveryPrefix = EnvStr("MQTT_DISCOVERY_PREFIX") ?? haosString("MQTT_DISCOVERY_PREFIX") ?? fromYaml.DiscoveryPrefix,
            BaseTopic = EnvStr("MQTT_BASE_TOPIC") ?? haosString("MQTT_BASE_TOPIC") ?? fromYaml.BaseTopic,
        };
    }
}
