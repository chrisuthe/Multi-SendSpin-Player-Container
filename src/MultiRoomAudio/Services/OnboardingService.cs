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
/// Uses YamlFileService for persistence.
/// </summary>
public class OnboardingService : YamlFileService<OnboardingState>
{
    private readonly ConfigurationService _config;

    public OnboardingService(
        ILogger<OnboardingService> logger,
        EnvironmentService environment,
        ConfigurationService config)
        : base(environment.OnboardingConfigPath, logger)
    {
        _config = config;
        Load();
    }

    /// <summary>
    /// Current onboarding state.
    /// </summary>
    public OnboardingState State => Data;

    /// <summary>
    /// Whether onboarding has been completed.
    /// </summary>
    public bool IsCompleted => Data.Completed;

    /// <summary>
    /// Whether onboarding should be shown (not completed and no players configured).
    /// </summary>
    public bool ShouldShowOnboarding => !Data.Completed && !_config.HasPlayers;

    /// <inheritdoc />
    protected override void OnDataLoaded()
    {
        Logger.LogDebug("Loaded onboarding state: Completed={Completed}", Data.Completed);
    }

    /// <summary>
    /// Mark onboarding as completed.
    /// </summary>
    public void MarkCompleted(int devicesConfigured = 0, int playersCreated = 0)
    {
        Lock.EnterWriteLock();
        try
        {
            Data.Completed = true;
            Data.CompletedAt = DateTime.UtcNow;
            Data.DevicesConfigured = devicesConfigured;
            Data.PlayersCreated = playersCreated;
            Data.AppVersion = GetAppVersion();

            Logger.LogInformation(
                "Onboarding completed: {DeviceCount} devices configured, {PlayerCount} players created",
                devicesConfigured, playersCreated);
        }
        finally
        {
            Lock.ExitWriteLock();
        }

        Save();
    }

    /// <summary>
    /// Reset onboarding to allow re-running the wizard.
    /// </summary>
    public void Reset()
    {
        Lock.EnterWriteLock();
        try
        {
            // Reset by saving a new empty state
            Data.Completed = false;
            Data.CompletedAt = null;
            Data.DevicesConfigured = 0;
            Data.PlayersCreated = 0;
            Data.AppVersion = null;

            Logger.LogInformation("Onboarding state reset");
        }
        finally
        {
            Lock.ExitWriteLock();
        }

        Save();
    }

    /// <summary>
    /// Skip onboarding without completing it.
    /// Marks as completed but with zero devices/players.
    /// </summary>
    public void Skip()
    {
        Lock.EnterWriteLock();
        try
        {
            Data.Completed = true;
            Data.CompletedAt = DateTime.UtcNow;
            Data.DevicesConfigured = 0;
            Data.PlayersCreated = 0;
            Data.AppVersion = GetAppVersion();

            Logger.LogInformation("Onboarding skipped");
        }
        finally
        {
            Lock.ExitWriteLock();
        }

        Save();
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
            completed = Data.Completed,
            completedAt = Data.CompletedAt,
            devicesConfigured = Data.DevicesConfigured,
            playersCreated = Data.PlayersCreated,
            appVersion = Data.AppVersion,
            shouldShow = ShouldShowOnboarding
        };
    }
}
