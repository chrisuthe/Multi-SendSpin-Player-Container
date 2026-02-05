namespace MultiRoomAudio.Services;

/// <summary>
/// Coordinates startup initialization of all services as a background task.
/// Runs after Kestrel starts so the web UI is immediately available.
/// Phases execute sequentially to honor dependency ordering:
/// CardProfiles → CustomSinks → Devices → Players → Triggers.
/// </summary>
/// <remarks>
/// Dependencies are resolved lazily via IServiceProvider rather than constructor
/// injection. This prevents the DI container from constructing heavy singletons
/// (ConfigurationService, BackendFactory, PlayerManagerService, etc.) during
/// hosted-service resolution, which runs before Kestrel starts listening.
/// </remarks>
public class StartupOrchestrator : BackgroundService
{
    private readonly ILogger<StartupOrchestrator> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;

    // Lazily resolved after Kestrel is listening
    private StartupProgressService _progress = null!;
    private CardProfileService _cardProfiles = null!;
    private CustomSinksService _customSinks = null!;
    private PlayerManagerService _playerManager = null!;
    private TriggerService _triggers = null!;
    private HidButtonService _hidButtons = null!;

    public StartupOrchestrator(
        ILogger<StartupOrchestrator> logger,
        IHostApplicationLifetime lifetime,
        IServiceProvider services)
    {
        _logger = logger;
        _lifetime = lifetime;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Kestrel to be fully listening before starting initialization
        // so the web UI is available immediately to show startup progress
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = _lifetime.ApplicationStarted.Register(() => tcs.SetResult());
        await tcs.Task.WaitAsync(stoppingToken);

        // Now resolve services — their constructors (ConfigurationService, BackendFactory, etc.)
        // run here, after the web server is already serving pages
        _progress = _services.GetRequiredService<StartupProgressService>();
        _cardProfiles = _services.GetRequiredService<CardProfileService>();
        _customSinks = _services.GetRequiredService<CustomSinksService>();
        _playerManager = _services.GetRequiredService<PlayerManagerService>();
        _triggers = _services.GetRequiredService<TriggerService>();
        _hidButtons = _services.GetRequiredService<HidButtonService>();

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

            // Phase 6: Initialize HID button support for hardware volume/mute controls
            await RunPhaseAsync("hidbuttons", () => _hidButtons.InitializeAsync(stoppingToken), stoppingToken);

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

        // Services may not have been resolved yet if shutdown occurs during early startup
        if (_hidButtons != null) await _hidButtons.DisposeAsync();
        if (_triggers != null) await _triggers.ShutdownAsync(cancellationToken);
        if (_playerManager != null) await _playerManager.ShutdownAsync(cancellationToken);
        if (_customSinks != null) await _customSinks.ShutdownAsync(cancellationToken);
        // CardProfileService has no shutdown logic

        await base.StopAsync(cancellationToken);
    }
}
