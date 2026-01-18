using System.Text.Json;

namespace MultiRoomAudio.Services;

/// <summary>
/// Detects runtime environment (HAOS vs standalone Docker) and provides
/// appropriate paths and configuration for each deployment mode.
/// </summary>
/// <remarks>
/// This class is designed for Linux containers (Docker/HAOS). Some paths used for
/// detection and configuration (e.g., /proc, /data, /share) are Linux-specific and
/// will not exist on Windows or macOS development machines.
/// </remarks>
public class EnvironmentService
{
    private readonly ILogger<EnvironmentService> _logger;
    private readonly bool _isHaos;
    private readonly bool _isMockHardware;
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly Dictionary<string, JsonElement>? _haosOptions;

    public const string EnvStandalone = "standalone";
    public const string EnvHaos = "haos";

    private const string HaosOptionsFile = "/data/options.json";
    private const string HaosSupervisorTokenEnv = "SUPERVISOR_TOKEN";
    private const string MockHardwareEnv = "MOCK_HARDWARE";

    public EnvironmentService(ILogger<EnvironmentService> logger)
    {
        _logger = logger;

        _logger.LogDebug("Detecting runtime environment...");
        _isHaos = DetectHaos();
        _isMockHardware = DetectMockHardware();

        if (_isMockHardware)
        {
            _logger.LogInformation("MOCK_HARDWARE mode enabled - using simulated audio devices and relay board");
        }

        if (_isHaos)
        {
            _logger.LogDebug("HAOS markers detected, configuring for Home Assistant add-on mode");
            _haosOptions = LoadHaosOptions();
            _configPath = "/data";
            _logPath = "/share/multiroom-audio/logs";

            if (_haosOptions != null)
            {
                _logger.LogDebug("Loaded {OptionCount} options from HAOS configuration",
                    _haosOptions.Count);
            }
        }
        else
        {
            _logger.LogDebug("No HAOS markers found, configuring for standalone Docker mode");
            _haosOptions = null;
            _configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "/app/config";
            _logPath = Environment.GetEnvironmentVariable("LOG_PATH") ?? "/app/logs";

            _logger.LogDebug("CONFIG_PATH env: {ConfigPathEnv}",
                Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "(not set, using default)");
            _logger.LogDebug("LOG_PATH env: {LogPathEnv}",
                Environment.GetEnvironmentVariable("LOG_PATH") ?? "(not set, using default)");
        }
    }

    /// <summary>
    /// Whether running in Home Assistant OS add-on mode.
    /// </summary>
    public bool IsHaos => _isHaos;

    /// <summary>
    /// Whether mock hardware mode is enabled (for testing without real devices).
    /// When true, the application uses simulated audio devices and relay boards.
    /// </summary>
    public bool IsMockHardware => _isMockHardware;

    /// <summary>
    /// Current environment name ("haos" or "standalone").
    /// </summary>
    public string EnvironmentName => _isHaos ? EnvHaos : EnvStandalone;

    /// <summary>
    /// Path to configuration directory.
    /// </summary>
    public string ConfigPath => _configPath;

    /// <summary>
    /// Full path to players.yaml configuration file.
    /// </summary>
    public string PlayersConfigPath => Path.Combine(_configPath, "players.yaml");

    /// <summary>
    /// Full path to devices.yaml configuration file (device aliases and stable identifiers).
    /// </summary>
    public string DevicesConfigPath => Path.Combine(_configPath, "devices.yaml");

    /// <summary>
    /// Full path to onboarding.yaml configuration file (wizard state).
    /// </summary>
    public string OnboardingConfigPath => Path.Combine(_configPath, "onboarding.yaml");

    /// <summary>
    /// Path to log directory.
    /// </summary>
    public string LogPath => _logPath;

    /// <summary>
    /// Audio backend (always "pulse" - PulseAudio).
    /// </summary>
    public string AudioBackend => "pulse";

    /// <summary>
    /// Get HAOS option value by key.
    /// </summary>
    public T? GetHaosOption<T>(string key, T? defaultValue = default)
    {
        if (_haosOptions == null || !_haosOptions.TryGetValue(key, out var element))
            return defaultValue;

        try
        {
            return element.Deserialize<T>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to deserialize HAOS option '{Key}' as type {Type}, returning default value",
                key, typeof(T).Name);
            return defaultValue;
        }
    }

    /// <summary>
    /// Get the volume control method (always "pactl" for PulseAudio).
    /// </summary>
    public string VolumeControlMethod => "pactl";

    /// <summary>
    /// Ensure required directories exist.
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        _logger.LogDebug("Ensuring required directories exist");

        try
        {
            if (!Directory.Exists(_configPath))
            {
                Directory.CreateDirectory(_configPath);
                _logger.LogInformation("Created config directory: {ConfigPath}", _configPath);
            }
            else
            {
                _logger.LogDebug("Config directory exists: {ConfigPath}", _configPath);
            }

            if (!Directory.Exists(_logPath))
            {
                Directory.CreateDirectory(_logPath);
                _logger.LogInformation("Created log directory: {LogPath}", _logPath);
            }
            else
            {
                _logger.LogDebug("Log directory exists: {LogPath}", _logPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create directories. ConfigPath: {ConfigPath}, LogPath: {LogPath}",
                _configPath, _logPath);
        }
    }

    private bool DetectHaos()
    {
        // Check for HAOS-specific markers
        _logger.LogDebug("Checking for HAOS environment markers...");

        if (File.Exists(HaosOptionsFile))
        {
            _logger.LogDebug("HAOS detected: options file exists at {OptionsFile}", HaosOptionsFile);
            return true;
        }
        else
        {
            _logger.LogDebug("HAOS options file not found at {OptionsFile}", HaosOptionsFile);
        }

        var supervisorToken = Environment.GetEnvironmentVariable(HaosSupervisorTokenEnv);
        if (!string.IsNullOrEmpty(supervisorToken))
        {
            _logger.LogDebug("HAOS detected: {EnvVar} environment variable is set", HaosSupervisorTokenEnv);
            return true;
        }
        else
        {
            _logger.LogDebug("{EnvVar} environment variable not set", HaosSupervisorTokenEnv);
        }

        _logger.LogDebug("No HAOS environment markers found");
        return false;
    }

    private Dictionary<string, JsonElement>? LoadHaosOptions()
    {
        if (!File.Exists(HaosOptionsFile))
        {
            _logger.LogDebug("HAOS options file does not exist, skipping load");
            return null;
        }

        try
        {
            _logger.LogDebug("Loading HAOS options from {OptionsFile}", HaosOptionsFile);
            var json = File.ReadAllText(HaosOptionsFile);
            var options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            if (options != null)
            {
                _logger.LogDebug("Successfully loaded HAOS options: {Keys}",
                    string.Join(", ", options.Keys));
            }

            return options;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Failed to parse HAOS options JSON from {OptionsFile}. File may be malformed",
                HaosOptionsFile);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex,
                "Failed to read HAOS options file {OptionsFile}. Check file permissions",
                HaosOptionsFile);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error loading HAOS options from {OptionsFile}",
                HaosOptionsFile);
            return null;
        }
    }

    private bool DetectMockHardware()
    {
        var mockHardwareValue = Environment.GetEnvironmentVariable(MockHardwareEnv);
        if (string.IsNullOrEmpty(mockHardwareValue))
        {
            _logger.LogDebug("{EnvVar} environment variable not set", MockHardwareEnv);
            return false;
        }

        // Accept "true", "1", "yes" as truthy values (case-insensitive)
        var isMock = mockHardwareValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                     mockHardwareValue == "1" ||
                     mockHardwareValue.Equals("yes", StringComparison.OrdinalIgnoreCase);

        if (isMock)
        {
            _logger.LogDebug("{EnvVar} detected: {Value}", MockHardwareEnv, mockHardwareValue);
        }
        else
        {
            _logger.LogDebug("{EnvVar} set to {Value}, not enabling mock mode", MockHardwareEnv, mockHardwareValue);
        }

        return isMock;
    }
}
