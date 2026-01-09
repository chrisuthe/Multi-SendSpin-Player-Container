using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services;

/// <summary>
/// Onboarding state persisted to YAML.
/// </summary>
public class OnboardingState
{
    /// <summary>
    /// Whether the onboarding wizard has been completed.
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// When the onboarding was completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Number of devices configured during onboarding.
    /// </summary>
    public int DevicesConfigured { get; set; }

    /// <summary>
    /// Number of players created during onboarding.
    /// </summary>
    public int PlayersCreated { get; set; }

    /// <summary>
    /// Version of the app when onboarding was completed.
    /// </summary>
    public string? AppVersion { get; set; }
}

/// <summary>
/// Service for managing the onboarding wizard state.
/// </summary>
public class OnboardingService
{
    private readonly ILogger<OnboardingService> _logger;
    private readonly EnvironmentService _environment;
    private readonly ConfigurationService _config;
    private readonly string _onboardingConfigPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly object _lock = new();

    private OnboardingState _state = new();

    public OnboardingService(
        ILogger<OnboardingService> logger,
        EnvironmentService environment,
        ConfigurationService config)
    {
        _logger = logger;
        _environment = environment;
        _config = config;
        _onboardingConfigPath = environment.OnboardingConfigPath;

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        Load();
    }

    /// <summary>
    /// Current onboarding state.
    /// </summary>
    public OnboardingState State => _state;

    /// <summary>
    /// Whether onboarding has been completed.
    /// </summary>
    public bool IsCompleted => _state.Completed;

    /// <summary>
    /// Whether onboarding should be shown (not completed and no players configured).
    /// </summary>
    public bool ShouldShowOnboarding => !_state.Completed && !_config.HasPlayers;

    /// <summary>
    /// Load onboarding state from YAML file.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_onboardingConfigPath))
            {
                _logger.LogDebug("Onboarding config file does not exist, starting fresh");
                _state = new OnboardingState();
                return;
            }

            try
            {
                var yaml = File.ReadAllText(_onboardingConfigPath);
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    _state = new OnboardingState();
                    return;
                }

                _state = _deserializer.Deserialize<OnboardingState>(yaml) ?? new OnboardingState();
                _logger.LogDebug("Loaded onboarding state: Completed={Completed}", _state.Completed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load onboarding state from {Path}", _onboardingConfigPath);
                _state = new OnboardingState();
            }
        }
    }

    /// <summary>
    /// Save onboarding state to YAML file.
    /// </summary>
    public bool Save()
    {
        lock (_lock)
        {
            try
            {
                var yaml = _serializer.Serialize(_state);
                File.WriteAllText(_onboardingConfigPath, yaml);
                _logger.LogDebug("Saved onboarding state");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save onboarding state to {Path}", _onboardingConfigPath);
                return false;
            }
        }
    }

    /// <summary>
    /// Mark onboarding as completed.
    /// </summary>
    public void MarkCompleted(int devicesConfigured = 0, int playersCreated = 0)
    {
        lock (_lock)
        {
            _state.Completed = true;
            _state.CompletedAt = DateTime.UtcNow;
            _state.DevicesConfigured = devicesConfigured;
            _state.PlayersCreated = playersCreated;
            _state.AppVersion = GetAppVersion();

            _logger.LogInformation(
                "Onboarding completed: {DeviceCount} devices configured, {PlayerCount} players created",
                devicesConfigured, playersCreated);

            Save();
        }
    }

    /// <summary>
    /// Reset onboarding to allow re-running the wizard.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = new OnboardingState();
            _logger.LogInformation("Onboarding state reset");
            Save();
        }
    }

    /// <summary>
    /// Skip onboarding without completing it.
    /// Marks as completed but with zero devices/players.
    /// </summary>
    public void Skip()
    {
        lock (_lock)
        {
            _state.Completed = true;
            _state.CompletedAt = DateTime.UtcNow;
            _state.DevicesConfigured = 0;
            _state.PlayersCreated = 0;
            _state.AppVersion = GetAppVersion();

            _logger.LogInformation("Onboarding skipped");
            Save();
        }
    }

    /// <summary>
    /// Get the current app version.
    /// </summary>
    private static string GetAppVersion()
    {
        var assembly = typeof(OnboardingService).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Get onboarding status for API response.
    /// </summary>
    public object GetStatus()
    {
        return new
        {
            completed = _state.Completed,
            completedAt = _state.CompletedAt,
            devicesConfigured = _state.DevicesConfigured,
            playersCreated = _state.PlayersCreated,
            appVersion = _state.AppVersion,
            shouldShow = ShouldShowOnboarding
        };
    }
}
