namespace MultiRoomAudio.Services;

/// <summary>
/// Coordinates startup initialization of all services as a background task.
/// Runs after Kestrel starts so the web UI is immediately available.
/// Phases execute sequentially to honor dependency ordering:
/// CardProfiles → CustomSinks → Devices → Players → Triggers.
/// </summary>
public class StartupOrchestrator : BackgroundService
{
    private readonly ILogger<StartupOrchestrator> _logger;
    private readonly StartupProgressService _progress;
    private readonly CardProfileService _cardProfiles;
    private readonly CustomSinksService _customSinks;
    private readonly PlayerManagerService _playerManager;
    private readonly TriggerService _triggers;

    public StartupOrchestrator(
        ILogger<StartupOrchestrator> logger,
        StartupProgressService progress,
        CardProfileService cardProfiles,
        CustomSinksService customSinks,
        PlayerManagerService playerManager,
        TriggerService triggers)
    {
        _logger = logger;
        _progress = progress;
        _cardProfiles = cardProfiles;
        _customSinks = customSinks;
        _playerManager = playerManager;
        _triggers = triggers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StartupOrchestrator: beginning background initialization...");

        try
        {
            // Phase 1: Restore sound card profiles (must be before sinks)
            await RunPhaseAsync("profiles", () => _cardProfiles.InitializeAsync(stoppingToken), stoppingToken);

            // Phase 2: Load custom audio sinks (must be before players)
            await RunPhaseAsync("sinks", () => _customSinks.InitializeAsync(stoppingToken), stoppingToken);

            // Phase 3: Detect audio devices and set hardware volumes
            await RunPhaseAsync("devices", () => _playerManager.InitializeHardwareAsync(stoppingToken), stoppingToken);

            // Phase 4: Autostart configured players
            await RunPhaseAsync("players", () => _playerManager.AutostartPlayersAsync(stoppingToken), stoppingToken);

            // Phase 5: Initialize 12V trigger relay boards
            await RunPhaseAsync("triggers", () => _triggers.InitializeAsync(stoppingToken), stoppingToken);

            _logger.LogInformation("StartupOrchestrator: all phases complete");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("StartupOrchestrator: cancelled during shutdown");
        }
    }

    private async Task RunPhaseAsync(string phaseId, Func<Task> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _progress.SetPhase(phaseId, StartupPhaseStatus.InProgress);
        try
        {
            await action();
            _progress.SetPhase(phaseId, StartupPhaseStatus.Completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Let shutdown propagate
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup phase '{PhaseId}' failed", phaseId);
            _progress.SetPhase(phaseId, StartupPhaseStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Graceful shutdown: stop services in reverse dependency order.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StartupOrchestrator: shutting down services...");

        // Stop in reverse order
        await _triggers.ShutdownAsync(cancellationToken);
        await _playerManager.ShutdownAsync(cancellationToken);
        await _customSinks.ShutdownAsync(cancellationToken);
        // CardProfileService has no shutdown logic

        await base.StopAsync(cancellationToken);
    }
}
