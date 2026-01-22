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
    private readonly bool _enableAdvancedFormats;
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly Dictionary<string, JsonElement>? _haosOptions;

    public const string EnvStandalone = "standalone";
    public const string EnvHaos = "haos";

    private const string HaosOptionsFile = "/data/options.json";
    private const string HaosSupervisorTokenEnv = "SUPERVISOR_TOKEN";
    private const string MockHardwareEnv = "MOCK_HARDWARE";
    private const string AdvancedFormatsEnv = "ENABLE_ADVANCED_FORMATS";

    public EnvironmentService(ILogger<EnvironmentService> logger)
    {
        _logger = logger;

        _logger.LogDebug("Detecting runtime environment...");
        _isHaos = DetectHaos();

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

            // Try environment variables first, then default Docker path, then local fallback
            var configPathEnv = Environment.GetEnvironmentVariable("CONFIG_PATH");
            var logPathEnv = Environment.GetEnvironmentVariable("LOG_PATH");

            if (!string.IsNullOrEmpty(configPathEnv))
            {
                _configPath = configPathEnv;
                _logPath = logPathEnv ?? "/app/logs";
                _logger.LogDebug("Using CONFIG_PATH from environment: {ConfigPath}", _configPath);
            }
            else
            {
                // Try default Docker path first, fall back to local if not writable
                _configPath = "/app/config";
                _logPath = "/app/logs";

                if (!IsDirectoryWritable("/app"))
                {
                    // Fall back to local development paths
                    var localBasePath = Path.Combine(Directory.GetCurrentDirectory(), "test-config");
                    _configPath = Path.Combine(localBasePath, "config");
                    _logPath = Path.Combine(localBasePath, "logs");
                    _logger.LogDebug("Using local development paths (/app not writable)");
                }
            }

            _logger.LogDebug("CONFIG_PATH: {ConfigPath}", _configPath);
            _logger.LogDebug("LOG_PATH: {LogPath}", _logPath);
        }

        // Detect mock hardware after HAOS options are loaded (so we can check both env var and HAOS config)
        _isMockHardware = DetectMockHardware();

        if (_isMockHardware)
        {
            _logger.LogInformation("MOCK_HARDWARE mode enabled - using simulated audio devices and relay board");
        }

        // Detect advanced formats feature flag
        _enableAdvancedFormats = DetectAdvancedFormats();

        if (_enableAdvancedFormats)
        {
            _logger.LogInformation("ENABLE_ADVANCED_FORMATS mode enabled - per-player format selection available");
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
    /// Whether advanced audio formats feature is enabled (dev-only feature).
    /// When true, the application exposes per-player format selection UI and API endpoints.
    /// </summary>
    public bool EnableAdvancedFormats => _enableAdvancedFormats;

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
    /// Full path to mock_hardware.yaml configuration file.
    /// Only used when IsMockHardware is true.
    /// </summary>
    public string MockHardwareConfigPath => Path.Combine(_configPath, "mock_hardware.yaml");

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

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            // Check if directory exists or can be created
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                return true;
            }

            // Directory exists, check if we can write to it
            var testFile = Path.Combine(path, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
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
        // Check environment variable first (works for both Docker and HAOS)
        var mockHardwareValue = Environment.GetEnvironmentVariable(MockHardwareEnv);
        if (!string.IsNullOrEmpty(mockHardwareValue))
        {
            // Accept "true", "1", "yes" as truthy values (case-insensitive)
            var isMock = mockHardwareValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                         mockHardwareValue == "1" ||
                         mockHardwareValue.Equals("yes", StringComparison.OrdinalIgnoreCase);

            if (isMock)
            {
                _logger.LogDebug("{EnvVar} detected: {Value}", MockHardwareEnv, mockHardwareValue);
                return true;
            }
            else
            {
                _logger.LogDebug("{EnvVar} set to {Value}, not enabling mock mode", MockHardwareEnv, mockHardwareValue);
            }
        }
        else
        {
            _logger.LogDebug("{EnvVar} environment variable not set", MockHardwareEnv);
        }

        // Check HAOS options (for add-on UI toggle)
        if (_isHaos && _haosOptions != null)
        {
            if (_haosOptions.TryGetValue("mock_hardware", out var element))
            {
                try
                {
                    var haosValue = element.GetBoolean();
                    if (haosValue)
                    {
                        _logger.LogDebug("Mock hardware enabled via HAOS options (mock_hardware: true)");
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    _logger.LogWarning("HAOS option 'mock_hardware' is not a boolean value");
                }
            }
        }

        return false;
    }

    private bool DetectAdvancedFormats()
    {
        // Check environment variable first (works for both Docker and HAOS)
        var advancedFormatsValue = Environment.GetEnvironmentVariable(AdvancedFormatsEnv);
        if (!string.IsNullOrEmpty(advancedFormatsValue))
        {
            // Accept "true", "1", "yes" as truthy values (case-insensitive)
            var isEnabled = advancedFormatsValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                           advancedFormatsValue == "1" ||
                           advancedFormatsValue.Equals("yes", StringComparison.OrdinalIgnoreCase);

            if (isEnabled)
            {
                _logger.LogDebug("{EnvVar} detected: {Value}", AdvancedFormatsEnv, advancedFormatsValue);
                return true;
            }
            else
            {
                _logger.LogDebug("{EnvVar} set to {Value}, not enabling advanced formats", AdvancedFormatsEnv, advancedFormatsValue);
            }
        }
        else
        {
            _logger.LogDebug("{EnvVar} environment variable not set", AdvancedFormatsEnv);
        }

        // Check HAOS options (for add-on UI toggle)
        if (_isHaos && _haosOptions != null)
        {
            if (_haosOptions.TryGetValue("enable_advanced_formats", out var element))
            {
                try
                {
                    var haosValue = element.GetBoolean();
                    if (haosValue)
                    {
                        _logger.LogDebug("Advanced formats enabled via HAOS options (enable_advanced_formats: true)");
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    _logger.LogWarning("HAOS option 'enable_advanced_formats' is not a boolean value");
                }
            }
        }

        // Default: disabled
        return false;
    }
}
