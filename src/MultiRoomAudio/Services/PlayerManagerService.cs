using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Audio;
using MultiRoomAudio.Exceptions;
using MultiRoomAudio.Hubs;
using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
// Alias to disambiguate from MultiRoomAudio.Models.PlayerState (lifecycle enum)
using SdkPlayerState = Sendspin.SDK.Models.PlayerState;
using static MultiRoomAudio.Utilities.BackgroundTaskExecutor;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages the lifecycle of all Sendspin players.
/// Handles creation, connection, state management, and disposal.
/// Integrates with ConfigurationService for persistence and autostart.
/// </summary>
public class PlayerManagerService : IAsyncDisposable, IDisposable
{
    private readonly ILogger<PlayerManagerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConfigurationService _config;
    private readonly EnvironmentService _environment;
    private readonly IHubContext<PlayerStatusHub> _hubContext;
    private readonly VolumeCommandRunner _volumeRunner;
    private readonly BackendFactory _backendFactory;
    private readonly TriggerService _triggerService;
    private readonly ConcurrentDictionary<string, PlayerContext> _players = new();
    private readonly MdnsServerDiscovery _serverDiscovery;
    private bool _disposed;
    private readonly object _playersLock = new();

    /// <summary>
    /// Tracks players pending reconnection with their retry state.
    /// </summary>
    private readonly ConcurrentDictionary<string, ReconnectionState> _pendingReconnections = new();

    /// <summary>
    /// Cancellation token source for the reconnection background task.
    /// </summary>
    private CancellationTokenSource? _reconnectionCts;

    /// <summary>
    /// Background task that processes pending reconnections.
    /// </summary>
    private Task? _reconnectionTask;

    /// <summary>
    /// Whether continuous mDNS discovery is running to detect server reappearance.
    /// </summary>
    private bool _mdnsWatchActive;

    /// <summary>
    /// Signal to wake the reconnection loop immediately (e.g. when mDNS discovers a server).
    /// </summary>
    private readonly SemaphoreSlim _reconnectionSignal = new(0, 1);

    /// <summary>
    /// Cached server URI from mDNS discovery to avoid race conditions.
    /// </summary>
    private Uri? _cachedServerUri;

    /// <summary>
    /// Expiry time for the cached server URI.
    /// </summary>
    private DateTime _cachedServerUriExpiry = DateTime.MinValue;

    /// <summary>
    /// Lock for serializing mDNS discovery requests.
    /// </summary>
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);

    #region Constants

    /// <summary>
    /// Buffer capacity announced to the Sendspin server (in bytes).
    ///
    /// Per protocol spec: "buffer_capacity: max size in bytes of compressed audio
    /// messages in the buffer that are yet to be played"
    ///
    /// The server sends audio chunks as far ahead as this capacity allows.
    /// At typical Opus bitrates (~128kbps), 32MB = many minutes of audio.
    /// This large value ensures the server always has audio ready to send.
    /// </summary>
    private const int ServerAnnouncedBufferCapacityBytes = 32_000_000;

    /// <summary>
    /// Local circular buffer capacity for decompressed PCM audio (in milliseconds).
    ///
    /// This is the TimedAudioBuffer size - how much decoded audio we can hold locally.
    /// Must be large enough to handle network jitter and decode timing variations.
    ///
    /// Note: This is DIFFERENT from ServerAnnouncedBufferCapacityBytes which controls
    /// how far ahead the server sends compressed audio.
    /// </summary>
    private const int LocalBufferCapacityMs = 8000;

    /// <summary>
    /// Target buffer level for playback readiness (in milliseconds).
    ///
    /// Playback starts when the local buffer reaches 80% of this value (200ms).
    /// Lower values = faster playback start but more sensitive to jitter.
    ///
    /// This is NOT a buffer capacity - it's a threshold for when to START playing.
    /// The actual buffer can hold much more (see LocalBufferCapacityMs).
    ///
    /// With SDK's HasMinimalSync (2 clock measurements), typical startup is 300-500ms.
    /// </summary>
    private const int PlaybackStartThresholdMs = 250;

    /// <summary>
    /// Sync correction options tuned for PulseAudio's timing characteristics.
    /// Uses a high threshold for Tier 3 (frame drop/insert) to prefer smooth rate adjustment.
    /// </summary>
    private static readonly SyncCorrectionOptions PulseAudioSyncOptions = new()
    {
        // Use 4% max correction (matches CLI) for more responsive adjustment
        MaxSpeedCorrection = 0.04,

        // Set high threshold to prefer rate adjustment over frame drop/insert (Tier 3)
        // SDK handles sync correction via TimedAudioBuffer's rate adjustment
        ResamplingThresholdMicroseconds = 200_000,  // 200ms (vs default 15ms)

        // Re-anchor at 500ms (same as default)
        ReanchorThresholdMicroseconds = 500_000,

        // Standard startup grace period
        StartupGracePeriodMicroseconds = 500_000,

        // Grace window for scheduled start time - allows playback to begin when
        // within this threshold of the scheduled time. Without a grace window,
        // micro-jitter in timing can cause the "scheduled start not reached"
        // issue where playback never starts because it's always "just barely"
        // in the future.
        ScheduledStartGraceWindowMicroseconds = 10_000,  // 10ms (SDK default)
    };

    /// <summary>
    /// Timeout for mDNS server discovery.
    /// </summary>
    private static readonly TimeSpan MdnsDiscoveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to cache a discovered server URI before re-discovering.
    /// </summary>
    private static readonly TimeSpan CachedServerTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Timeout for graceful disposal of player resources.
    /// </summary>
    private static readonly TimeSpan DisposalTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum allowed length for player names.
    /// </summary>
    private const int MaxPlayerNameLength = 100;

    /// <summary>
    /// Pattern for valid player names.
    /// Allows any printable characters except control characters (supports international characters).
    /// </summary>
    private static readonly Regex ValidPlayerNamePattern = new(
        @"^[^\x00-\x1F\x7F]+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Initial delay before first reconnection attempt.
    /// </summary>
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum delay between reconnection attempts (caps exponential backoff).
    /// </summary>
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum number of reconnection attempts before giving up (0 = unlimited).
    /// </summary>
    private const int MaxReconnectAttempts = 0; // Unlimited - keep trying forever

    /// <summary>
    /// Grace period after connection during which volume updates from MA are ignored.
    /// This allows the startup volume to "win" the initial sync battle with Music Assistant.
    /// </summary>
    private static readonly TimeSpan VolumeGracePeriod = TimeSpan.FromSeconds(5);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Determines if a player is in an active state where it can receive commands.
    /// Active states include Starting, Connecting, Connected, Playing, and Buffering.
    /// </summary>
    /// <param name="state">The player state to check.</param>
    /// <returns>True if the player is in an active state.</returns>
    private static bool IsPlayerInActiveState(Models.PlayerState state)
    {
        return state == Models.PlayerState.Starting ||
               state == Models.PlayerState.Connecting ||
               state == Models.PlayerState.Connected ||
               state == Models.PlayerState.Playing ||
               state == Models.PlayerState.Buffering;
    }

    /// <summary>
    /// Disposes all resources for a player context in the correct order.
    /// Used by RemoveAndDisposePlayerAsync, Dispose, and DisposeAsync.
    /// Ensures all resources are disposed even if some throw exceptions.
    /// </summary>
    private static async Task DisposePlayerContextAsync(PlayerContext context)
    {
        List<Exception>? exceptions = null;

        // Dispose each resource, collecting any exceptions
        // Continue disposing remaining resources even if one fails
        try
        {
            await context.Client.DisposeAsync();
        }
        catch (Exception ex)
        {
            exceptions ??= new List<Exception>();
            exceptions.Add(ex);
        }

        try
        {
            await context.Pipeline.DisposeAsync();
        }
        catch (Exception ex)
        {
            exceptions ??= new List<Exception>();
            exceptions.Add(ex);
        }

        try
        {
            await context.Player.DisposeAsync();
        }
        catch (Exception ex)
        {
            exceptions ??= new List<Exception>();
            exceptions.Add(ex);
        }

        try
        {
            await context.Connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            exceptions ??= new List<Exception>();
            exceptions.Add(ex);
        }

        // Dispose the CancellationTokenSource to release internal resources
        try
        {
            context.Cts.Dispose();
        }
        catch (Exception ex)
        {
            exceptions ??= new List<Exception>();
            exceptions.Add(ex);
        }

        // If any exceptions occurred, throw an AggregateException
        if (exceptions != null)
        {
            throw new AggregateException("One or more errors occurred during player context disposal", exceptions);
        }
    }

    /// <summary>
    /// Pushes the current configured volume to the server.
    /// Called on connection and when playback starts to ensure server has correct volume.
    /// This addresses the issue where the SDK sends volume:100 in the initial hello,
    /// overriding the user's configured volume.
    /// </summary>
    private async Task PushVolumeToServerAsync(string name, PlayerContext context)
    {
        if (!IsPlayerInActiveState(context.State))
        {
            _logger.LogDebug("Skipping volume push for '{Name}' - not in active state ({State})",
                name, context.State);
            return;
        }

        try
        {
            // Prevent feedback loop with PlayerStateChanged handler
            context.IsUpdatingFromServer = true;
            _logger.LogInformation("VOLUME [PushToServer] Player '{Name}': {Volume}%",
                name, context.Config.Volume);
            await context.Client.SetVolumeAsync(context.Config.Volume);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push volume to server for player '{Name}'", name);
        }
        finally
        {
            context.IsUpdatingFromServer = false;
        }
    }

    /// <summary>
    /// Hardware volume percentage used for all audio outputs.
    /// Set to 80% to avoid clipping while allowing server to control actual volume.
    /// </summary>
    private const int HardwareVolumePercent = 80;

    #endregion

    /// <summary>
    /// Internal context holding all objects for a player instance.
    /// </summary>
    private record PlayerContext(
        ISendspinClient Client,
        SendspinConnection Connection,
        IAudioPipeline Pipeline,
        IAudioPlayer Player,
        IClockSynchronizer ClockSync,
        ClientCapabilities Capabilities,
        PlayerConfig Config,
        DateTime CreatedAt,
        CancellationTokenSource Cts,
        DeviceCapabilities? DeviceCapabilities = null,
        AudioDevice? CachedDevice = null
    )
    {
        public Models.PlayerState State { get; set; } = Models.PlayerState.Created;
        public string? ErrorMessage { get; set; }
        public DateTime? ConnectedAt { get; set; }
        public int InitialVolume { get; init; } // Store initial volume to detect resets
        public long SamplesPlayed { get; set; }
        public bool? LastConfirmedMuted { get; set; } // Track last mute state echoed to server

        // Server info captured during connection/handshake
        public string? ServerName { get; set; }       // Friendly name from MA handshake (server/hello)
        public string? ConnectedAddress { get; set; } // IP:port we connected to

        // Event handler references for proper cleanup (prevents memory leaks)
        public EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateHandler { get; set; }
        public EventHandler<AudioPipelineState>? PipelineStateHandler { get; set; }
        public EventHandler<AudioPipelineError>? PipelineErrorHandler { get; set; }
        public EventHandler<AudioPlayerError>? PlayerErrorHandler { get; set; }
        public EventHandler<GroupState>? GroupStateHandler { get; set; }
        // SDK 5.4.0: Handler for individual player volume/mute commands
        public EventHandler<SdkPlayerState>? PlayerStateHandler { get; set; }
        // Flag to prevent feedback loops when updating server
        public bool IsUpdatingFromServer { get; set; }
    }

    /// <summary>
    /// Tracks reconnection state for a player.
    /// </summary>
    private record ReconnectionState(
        PlayerConfiguration Config,
        int RetryCount = 0,
        DateTime? NextRetryTime = null,
        bool WasUserStopped = false,
        bool MdnsOnly = false
    );

    /// <summary>
    /// Holds SDK components created during player setup.
    /// Used internally to pass components between helper methods.
    /// </summary>
    private record PlayerComponents(
        ClientCapabilities Capabilities,
        IClockSynchronizer ClockSync,
        IAudioPlayer Player,
        IAudioPipeline Pipeline,
        SendspinConnection Connection,
        ISendspinClient Client,
        DeviceCapabilities? DeviceCapabilities
    );

    public PlayerManagerService(
        ILogger<PlayerManagerService> logger,
        ILoggerFactory loggerFactory,
        ConfigurationService config,
        EnvironmentService environment,
        IHubContext<PlayerStatusHub> hubContext,
        VolumeCommandRunner volumeRunner,
        BackendFactory backendFactory,
        TriggerService triggerService)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        _environment = environment;
        _hubContext = hubContext;
        _volumeRunner = volumeRunner;
        _backendFactory = backendFactory;
        _triggerService = triggerService;
        _serverDiscovery = new MdnsServerDiscovery(
            loggerFactory.CreateLogger<MdnsServerDiscovery>());
    }

    /// <summary>
    /// Initializes all audio device hardware volumes to a safe level (80% or configured max).
    /// Called by StartupOrchestrator during background initialization.
    /// Devices with configured MaxVolume limits in devices.yaml use their limit instead.
    /// </summary>
    public async Task InitializeHardwareAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing hardware volumes to {Volume}%...", HardwareVolumePercent);

        try
        {
            var devices = _backendFactory.GetOutputDevices().ToList();

            if (devices.Count == 0)
            {
                _logger.LogWarning("No audio output devices found for volume initialization");
                return;
            }

            _logger.LogInformation("Found {Count} audio output device(s)", devices.Count);

            // Track all devices with identifiers for persistence
            var newDevicesFound = false;
            foreach (var device in devices)
            {
                if (device.Identifiers != null)
                {
                    var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                    if (_config.EnsureDeviceTracked(deviceKey, device))
                    {
                        newDevicesFound = true;
                    }
                }
            }

            // Save once if any new devices were discovered
            if (newDevicesFound)
            {
                _config.SaveDevices();
            }

            // Get device configurations to check for volume limits
            var deviceConfigs = _config.GetAllDeviceConfigurations();

            foreach (var device in devices)
            {
                try
                {
                    // Determine volume to apply: use configured max if set, otherwise default 80%
                    var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                    int volumeToApply;
                    string volumeSource;

                    if (deviceConfigs.TryGetValue(deviceKey, out var config) && config.MaxVolume.HasValue)
                    {
                        // User has explicitly configured a max volume - honor it
                        volumeToApply = config.MaxVolume.Value;
                        volumeSource = "configured max";
                    }
                    else
                    {
                        // No configured limit - apply default hardware volume
                        volumeToApply = HardwareVolumePercent;
                        volumeSource = "default";
                    }

                    var success = await _backendFactory.SetVolumeAsync(device.Id, volumeToApply, cancellationToken);
                    if (success)
                    {
                        _logger.LogInformation("VOLUME [Init] Device '{Name}' ({Id}): set to {Volume}% ({Source})",
                            device.Name, device.Id, volumeToApply, volumeSource);
                    }
                    else
                    {
                        _logger.LogWarning("VOLUME [Init] Device '{Name}' ({Id}): failed to set volume",
                            device.Name, device.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "VOLUME [Init] Device '{Name}' ({Id}): error setting volume",
                        device.Name, device.Id);
                }
            }

            _logger.LogInformation("Hardware volume initialization complete");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize hardware volumes (non-fatal)");
        }
    }

    /// <summary>
    /// Autostarts configured players and begins the background reconnection task.
    /// Called by StartupOrchestrator during background initialization.
    /// </summary>
    public async Task AutostartPlayersAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PlayerManagerService starting...");

        // Autostart configured players (creates player objects; connections are async)
        var autostartPlayers = _config.GetAutostartPlayers();
        if (autostartPlayers.Count == 0)
        {
            _logger.LogInformation("No players configured for autostart");
        }
        else
        {
            _logger.LogInformation("Found {AutostartCount} players configured for autostart",
                autostartPlayers.Count);

            foreach (var playerConfig in autostartPlayers)
            {
                await TryAutostartPlayerAsync(playerConfig, cancellationToken);
            }
        }

        // Start background task: check for failed connections then run reconnection loop.
        // This runs AFTER the startup phase completes so the UI becomes usable immediately.
        _reconnectionCts = new CancellationTokenSource();
        _reconnectionTask = PostAutostartAndReconnectAsync(autostartPlayers, _reconnectionCts.Token);

        _logger.LogInformation("PlayerManagerService started with {PlayerCount} active players, {PendingCount} pending reconnection",
            _players.Count, _pendingReconnections.Count);
    }

    /// <summary>
    /// Background task that waits for initial connections to settle, then runs the reconnection loop.
    /// Runs after the startup "players" phase completes so the UI is immediately usable.
    /// </summary>
    private async Task PostAutostartAndReconnectAsync(
        IReadOnlyList<PlayerConfiguration> autostartPlayers,
        CancellationToken cancellationToken)
    {
        // Check for failed connections after mDNS discovery timeout
        if (autostartPlayers.Count > 0)
        {
            await CheckForFailedConnectionsAsync(autostartPlayers, cancellationToken);
        }

        // Then run the reconnection loop
        await ProcessReconnectionsAsync(cancellationToken);
    }

    /// <summary>
    /// Attempts to autostart a single player.
    /// </summary>
    private async Task TryAutostartPlayerAsync(PlayerConfiguration playerConfig, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Autostarting player {PlayerName} on device {Device} with server {Server}",
            playerConfig.Name,
            playerConfig.PortAudioDeviceIndex?.ToString() ?? playerConfig.Device ?? "(default)",
            playerConfig.Server ?? "(auto-discover)");

        try
        {
            var request = new PlayerCreateRequest
            {
                Name = playerConfig.Name,
                Device = playerConfig.PortAudioDeviceIndex?.ToString() ?? playerConfig.Device,
                ClientId = ClientIdGenerator.Generate(playerConfig.Name),
                ServerUrl = playerConfig.Server,
                Volume = playerConfig.Volume ?? 100,
                DelayMs = playerConfig.DelayMs,
                AdvertisedFormat = playerConfig.AdvertisedFormat,
                Persist = false // Already persisted, don't re-save
            };

            await CreatePlayerAsync(request, cancellationToken);
            _logger.LogInformation("Player {PlayerName} autostarted successfully", playerConfig.Name);
        }
        catch (ArgumentException ex)
        {
            // Device validation failed - don't auto-reconnect, let user fix config
            _logger.LogError(ex,
                "Player {PlayerName} failed to start due to configuration error: {Message}. " +
                "Player will remain in error state until manually fixed.",
                playerConfig.Name, ex.Message);
        }
        catch (Exception ex)
        {
            // Network/server issues - queue for reconnection
            _logger.LogWarning(ex,
                "Failed to autostart player {PlayerName}, queuing for reconnection. Device: {Device}, Server: {Server}",
                playerConfig.Name,
                playerConfig.Device ?? "(default)",
                playerConfig.Server ?? "(auto-discover)");

            QueueForReconnection(playerConfig, mdnsOnly: string.IsNullOrEmpty(playerConfig.Server));
        }
    }

    /// <summary>
    /// Waits for mDNS discovery to complete and queues any players that failed to connect.
    /// </summary>
    private async Task CheckForFailedConnectionsAsync(
        IReadOnlyList<PlayerConfiguration> autostartPlayers,
        CancellationToken cancellationToken)
    {
        // Wait for all mDNS discovery attempts to complete (6s timeout + 2s buffer)
        _logger.LogDebug("Waiting for connection attempts to complete...");
        await Task.Delay(8000, cancellationToken);

        foreach (var playerConfig in autostartPlayers)
        {
            if (_players.TryGetValue(playerConfig.Name, out var context))
            {
                // Queue for reconnection if player is in error state and never connected
                if (context.State == Models.PlayerState.Error && context.ConnectedAt == null)
                {
                    var isMdnsOnly = string.IsNullOrEmpty(playerConfig.Server);
                    _logger.LogWarning(
                        isMdnsOnly
                            ? "Player '{PlayerName}' waiting for mDNS discovery (no server configured)"
                            : "Player '{PlayerName}' failed to connect during autostart, queuing for reconnection",
                        playerConfig.Name);
                    QueueForReconnection(playerConfig, mdnsOnly: isMdnsOnly);
                }
            }
        }
    }

    /// <summary>
    /// Gracefully stops all players and the reconnection task.
    /// Called by StartupOrchestrator during shutdown.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PlayerManagerService stopping with {PlayerCount} active players, {PendingCount} pending reconnection...",
            _players.Count, _pendingReconnections.Count);

        // Stop reconnection task first
        _reconnectionCts?.Cancel();
        if (_reconnectionTask != null)
        {
            try
            {
                await _reconnectionTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Reconnection task did not stop in time");
            }
            catch (OperationCanceledException) { }
        }
        _pendingReconnections.Clear();

        var playerNames = _players.Keys.ToList();
        foreach (var name in playerNames)
        {
            _logger.LogDebug("Stopping player {PlayerName}...", name);
        }

        // On service shutdown, fully dispose all players
        var tasks = playerNames.Select(name => RemoveAndDisposePlayerAsync(name)).ToArray();
        await Task.WhenAll(tasks);

        _logger.LogInformation("PlayerManagerService stopped, all players disposed");
    }

    /// <summary>
    /// Validates a player name to ensure it meets security and format requirements.
    /// </summary>
    /// <param name="name">The player name to validate.</param>
    /// <param name="errorMessage">Error message if validation fails.</param>
    /// <returns>True if the player name is valid.</returns>
    public static bool ValidatePlayerName(string? name, out string? errorMessage)
    {
        errorMessage = null;

        // Check for null or empty
        if (string.IsNullOrWhiteSpace(name))
        {
            errorMessage = "Player name cannot be empty or whitespace-only.";
            return false;
        }

        // Check maximum length
        if (name.Length > MaxPlayerNameLength)
        {
            errorMessage = $"Player name exceeds maximum length of {MaxPlayerNameLength} characters.";
            return false;
        }

        // Validate against allowed character pattern (no control characters)
        if (!ValidPlayerNamePattern.IsMatch(name))
        {
            errorMessage = "Player name cannot contain control characters.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates and starts a new player with the given configuration.
    /// </summary>
    public async Task<PlayerResponse> CreatePlayerAsync(PlayerCreateRequest request, CancellationToken ct = default)
    {
        // Validate player name
        if (!ValidatePlayerName(request.Name, out var nameError))
        {
            throw new ArgumentException(nameError, nameof(request.Name));
        }

        // Early disposed check before expensive setup
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PlayerManagerService));
        }

        // Validate device if specified
        if (!_backendFactory.ValidateDevice(request.Device, out var deviceError))
        {
            throw new ArgumentException(deviceError);
        }

        _logger.LogInformation("Creating player '{Name}' with device '{Device}'",
            request.Name, request.Device ?? "default");

        try
        {
            // Phase 1: Create all SDK components
            var components = CreateSdkComponents(request);

            // Phase 2: Create config and context
            var config = new PlayerConfig
            {
                Name = request.Name,
                DeviceId = request.Device,
                ClientId = components.Capabilities.ClientId,
                ServerUrl = request.ServerUrl,
                Volume = request.Volume,
                DelayMs = request.DelayMs,
                AdvertisedFormat = request.AdvertisedFormat
            };

            var cts = new CancellationTokenSource();

            // Cache device info once at creation time for stats display
            // This avoids running pactl every 250ms when stats panel is open
            var cachedDevice = !string.IsNullOrEmpty(request.Device)
                ? _backendFactory.GetDevice(request.Device)
                : null;

            var context = new PlayerContext(
                components.Client,
                components.Connection,
                components.Pipeline,
                components.Player,
                components.ClockSync,
                components.Capabilities,
                config,
                DateTime.UtcNow,
                cts,
                components.DeviceCapabilities,
                cachedDevice)
            {
                State = Models.PlayerState.Created,
                InitialVolume = request.Volume
            };

            // Phase 3: Wire up events
            WireEvents(request.Name, context);

            // Phase 4: Persist configuration if requested
            // This ensures config is saved before player runs, so reboot behavior is consistent
            if (request.Persist)
            {
                var persistConfig = new PlayerConfiguration
                {
                    Name = request.Name,
                    Device = request.Device ?? "",
                    Provider = "sendspin",
                    Autostart = true,
                    DelayMs = request.DelayMs,
                    Server = request.ServerUrl,
                    Volume = request.Volume,
                    AdvertisedFormat = request.AdvertisedFormat
                };
                _config.SetPlayer(request.Name, persistConfig);
                _config.Save();
                _logger.LogDebug("Persisted configuration for player '{Name}'", request.Name);
            }

            // Phase 5: Register player atomically
            // Handles race condition where another thread created a player with the same name
            if (!_players.TryAdd(request.Name, context))
            {
                await HandleRegistrationFailureAsync(request.Name, context, request.Persist);
                throw new EntityAlreadyExistsException("Player", request.Name);
            }

            // Phase 6: Initialize and start connection
            InitializeAndConnectPlayer(request.Name, context, request.DelayMs);

            return CreateResponse(request.Name, context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create player '{Name}'", request.Name);
            throw;
        }
    }

    /// <summary>
    /// Creates all SDK components needed for a player.
    /// </summary>
    /// <param name="request">The player creation request.</param>
    /// <returns>Record containing all SDK components.</returns>
    private PlayerComponents CreateSdkComponents(PlayerCreateRequest request)
    {
        // Probe device capabilities (used for reporting in Stats for Nerds)
        var deviceCapabilities = _backendFactory.GetDeviceCapabilities(request.Device);

        // Apply format filtering (always defaults to flac-48000 for maximum compatibility)
        var audioFormats = GetDefaultFormats();
        audioFormats = FilterFormatsByPreference(audioFormats, request.AdvertisedFormat);

        // Create capabilities with player role
        var clientCapabilities = new ClientCapabilities
        {
            ClientId = request.ClientId ?? GenerateClientId(request.Name),
            ClientName = request.Name,
            Roles = new List<string> { "controller@v1", "player@v1", "metadata@v1" },
            AudioFormats = audioFormats,
            BufferCapacity = ServerAnnouncedBufferCapacityBytes,
            InitialVolume = request.Volume,
            InitialMuted = false // Players start unmuted
        };

        // Create clock synchronizer
        var clockSync = new KalmanClockSynchronizer(
            _loggerFactory.CreateLogger<KalmanClockSynchronizer>());

        // Create audio player using the appropriate backend
        var player = _backendFactory.CreatePlayer(request.Device, _loggerFactory);

        // Create audio pipeline with proper factories
        var decoderFactory = new AudioDecoderFactory();
        var pipeline = new AudioPipeline(
            _loggerFactory.CreateLogger<AudioPipeline>(),
            decoderFactory,
            clockSync,
            bufferFactory: (format, sync) =>
            {
                var buffer = new TimedAudioBuffer(
                    format,
                    sync,
                    bufferCapacityMs: LocalBufferCapacityMs,
                    syncOptions: PulseAudioSyncOptions);
                buffer.TargetBufferMilliseconds = PlaybackStartThresholdMs;
                return buffer;
            },
            playerFactory: () => player,
            sourceFactory: (buffer, timeFunc) =>
            {
                return new BufferedAudioSampleSource(
                    buffer,
                    timeFunc,
                    _loggerFactory.CreateLogger<BufferedAudioSampleSource>());
            },
            waitForConvergence: true,
            convergenceTimeoutMs: 1000);

        // Create WebSocket connection.
        // AutoReconnect disabled: the app's own reconnection logic handles recovery
        // with fresh mDNS discovery and clean player contexts (see QueueForReconnection).
        var connection = new SendspinConnection(
            _loggerFactory.CreateLogger<SendspinConnection>(),
            new ConnectionOptions { AutoReconnect = false });

        // Create SDK client
        var client = new SendspinClientService(
            _loggerFactory.CreateLogger<SendspinClientService>(),
            connection,
            clockSync,
            clientCapabilities,
            pipeline);

        return new PlayerComponents(
            clientCapabilities,
            clockSync,
            player,
            pipeline,
            connection,
            client,
            deviceCapabilities);
    }

    /// <summary>
    /// Handles registration failure by rolling back config and cleaning up context.
    /// </summary>
    /// <param name="name">Player name.</param>
    /// <param name="context">Player context to dispose.</param>
    /// <param name="wasPersisted">Whether config was persisted and needs rollback.</param>
    private async Task HandleRegistrationFailureAsync(string name, PlayerContext context, bool wasPersisted)
    {
        _logger.LogWarning("Race condition: player '{Name}' was created by another thread", name);

        if (wasPersisted)
        {
            try
            {
                _config.DeletePlayer(name);
                _config.Save();
            }
            catch (Exception configEx)
            {
                _logger.LogWarning(configEx, "Failed to roll back config for '{Name}'", name);
            }
        }

        UnwireEvents(context);
        context.Cts.Cancel();

        try
        {
            await DisposePlayerContextAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing orphaned context for '{Name}'", name);
        }
    }

    /// <summary>
    /// Initializes a registered player and starts its connection.
    /// Called after the player has been successfully added to _players.
    /// </summary>
    /// <param name="name">Player name.</param>
    /// <param name="context">Player context.</param>
    /// <param name="delayMs">Delay offset in milliseconds.</param>
    private void InitializeAndConnectPlayer(string name, PlayerContext context, int delayMs)
    {
        // Apply startup volume locally - player is authoritative for its own volume
        // Hardware volume is set to 80% on container startup to avoid clipping
        context.Player.Volume = context.Config.Volume / 100.0f;
        _logger.LogInformation("VOLUME [Create] Player '{Name}': startup volume {Volume}% applied locally",
            name, context.Config.Volume);

        // Apply delay offset from user configuration
        context.ClockSync.StaticDelayMs = delayMs;
        if (delayMs != 0)
        {
            _logger.LogInformation("Delay offset for '{Name}': {DelayMs}ms", name, delayMs);
        }

        // Start connection in background with proper error handling
        FireAndForget(
            ConnectPlayerWithErrorHandlingAsync(name, context, context.Cts.Token),
            $"Connection setup for player '{name}'", _logger);

        // Broadcast status update to all clients
        FireAndForget(BroadcastStatusAsync(), $"Status broadcast after creating player '{name}'", _logger);
    }

    /// <summary>
    /// Creates multiple players in a batch operation.
    /// Validates all players, saves configurations, and starts them.
    /// </summary>
    /// <param name="players">The list of players to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing lists of created, started, and failed players.</returns>
    public async Task<BatchCreatePlayersResult> BatchCreatePlayersAsync(
        List<BatchPlayerRequest> players,
        CancellationToken ct = default)
    {
        var created = new List<string>();
        var started = new List<string>();
        var failed = new List<BatchPlayerFailure>();

        // Validate and create configurations for each player
        foreach (var playerReq in players)
        {
            try
            {
                // Validate player name is not empty
                if (string.IsNullOrWhiteSpace(playerReq.Name))
                {
                    failed.Add(new BatchPlayerFailure(playerReq.Name ?? "(empty)", "Player name is required"));
                    continue;
                }

                // Validate player name format
                if (!ValidatePlayerName(playerReq.Name, out var nameError))
                {
                    failed.Add(new BatchPlayerFailure(playerReq.Name, nameError!));
                    continue;
                }

                // Check if player already exists
                if (_config.PlayerExists(playerReq.Name))
                {
                    failed.Add(new BatchPlayerFailure(playerReq.Name, "Player already exists"));
                    continue;
                }

                // Create player configuration
                var playerConfig = new PlayerConfiguration
                {
                    Name = playerReq.Name,
                    Device = playerReq.Device ?? string.Empty,
                    Volume = playerReq.Volume ?? 75,
                    Autostart = playerReq.Autostart ?? true,
                    Provider = "sendspin"
                };

                _config.SetPlayer(playerReq.Name, playerConfig);
                created.Add(playerReq.Name);

                _logger.LogInformation("Created player configuration from batch: {PlayerName}", playerReq.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create player configuration {PlayerName}", playerReq.Name);
                failed.Add(new BatchPlayerFailure(playerReq.Name ?? "(unknown)", ex.Message));
            }
        }

        // Save all configurations at once
        if (created.Count > 0)
        {
            _config.Save();
        }

        // Start each created player
        // Note: Autostart won't trigger since the app is already running
        foreach (var playerName in created)
        {
            try
            {
                var playerConfig = _config.GetPlayer(playerName);
                if (playerConfig == null)
                    continue;

                var createRequest = new PlayerCreateRequest
                {
                    Name = playerName,
                    Device = playerConfig.Device,
                    Volume = playerConfig.Volume ?? 75,
                    AdvertisedFormat = playerConfig.AdvertisedFormat
                };

                await CreatePlayerAsync(createRequest, ct);
                started.Add(playerName);

                _logger.LogInformation("Started player from batch: {PlayerName}", playerName);
            }
            catch (Exception ex)
            {
                // Don't add to failed - the player was created (config saved), just not started
                // It will start on next restart
                _logger.LogWarning(ex, "Failed to start player {PlayerName} (config saved, will start on restart)", playerName);
            }
        }

        return new BatchCreatePlayersResult(created, started, failed);
    }

    /// <summary>
    /// Gets the current status of a player.
    /// </summary>
    public PlayerResponse? GetPlayer(string name)
    {
        if (!_players.TryGetValue(name, out var context))
            return null;

        return CreateResponse(name, context);
    }

    /// <summary>
    /// Gets all players, including those that failed to start.
    /// </summary>
    public PlayersListResponse GetAllPlayers()
    {
        var responses = new List<PlayerResponse>();

        // Get all configured players from config
        var configuredPlayers = _config.Players;

        foreach (var (name, config) in configuredPlayers)
        {
            if (_players.TryGetValue(name, out var context))
            {
                // Player is active - use live status
                responses.Add(CreateResponse(name, context));
            }
            else
            {
                // Player is configured but not active (failed to start or was stopped)
                // Check if it's pending reconnection
                var isPendingReconnection = _pendingReconnections.TryGetValue(name, out var reconnectState);
                Models.PlayerState state;
                string errorMessage;
                if (isPendingReconnection && !reconnectState!.WasUserStopped)
                {
                    if (reconnectState.MdnsOnly)
                    {
                        state = Models.PlayerState.WaitingForServer;
                        errorMessage = "Waiting for mDNS discovery...";
                    }
                    else
                    {
                        state = Models.PlayerState.Reconnecting;
                        errorMessage = $"Reconnecting... (attempt {reconnectState.RetryCount})";
                    }
                }
                else
                {
                    state = Models.PlayerState.Error;
                    errorMessage = "Player not running. Device may be unavailable or misconfigured.";
                }

                // Return a placeholder response so user can edit/reconfigure it
                var volume = config.Volume ?? 100;
                responses.Add(new PlayerResponse(
                    Name: name,
                    State: state,
                    Device: config.Device,
                    ClientId: ClientIdGenerator.Generate(name),
                    ServerUrl: config.Server,
                    ServerName: null,        // Not connected, no server name yet
                    ConnectedAddress: null,  // Not connected, no address yet
                    Volume: volume,
                    StartupVolume: volume, // For non-running players, startup volume = config volume
                    IsMuted: false,
                    DelayMs: config.DelayMs,
                    OutputLatencyMs: 0,
                    CreatedAt: DateTime.MinValue,
                    ConnectedAt: null,
                    ErrorMessage: errorMessage,
                    IsClockSynced: false,
                    Metrics: null,
                    DeviceCapabilities: null,
                    IsPendingReconnection: isPendingReconnection,
                    ReconnectionAttempts: isPendingReconnection ? reconnectState!.RetryCount : null,
                    NextReconnectionAttempt: isPendingReconnection ? reconnectState!.NextRetryTime : null,
                    AdvertisedFormat: config.AdvertisedFormat,
                    CurrentTrack: null
                ));
            }
        }

        var players = responses
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PlayersListResponse(players, players.Count);
    }

    /// <summary>
    /// Hot-switches the audio device for a player without stopping playback.
    /// </summary>
    public async Task<bool> SwitchDeviceAsync(string name, string? newDeviceId, CancellationToken ct = default)
    {
        if (!_players.TryGetValue(name, out var context))
            return false;

        // Validate new device
        if (!_backendFactory.ValidateDevice(newDeviceId, out var error))
        {
            throw new ArgumentException(error);
        }

        _logger.LogInformation("Hot-switching player '{Name}' to device '{Device}'",
            name, newDeviceId ?? "default");

        try
        {
            await context.Pipeline.SwitchDeviceAsync(newDeviceId, ct);
            context.Config.DeviceId = newDeviceId;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch device for player '{Name}'", name);
            throw;
        }
    }

    /// <summary>
    /// Sets the volume for a player (0-100).
    /// Updates local config and notifies server. Hardware volume is fixed at 80% on startup.
    /// </summary>
    public Task<bool> SetVolumeAsync(string name, int volume, CancellationToken ct = default)
    {
        if (!_players.TryGetValue(name, out var context))
            return Task.FromResult(false);

        volume = Math.Clamp(volume, 0, 100);
        _logger.LogInformation("VOLUME [Set] Player '{Name}': {Volume}%", name, volume);

        // 1. Update local config (always)
        context.Config.Volume = volume;

        // 2. Apply volume locally - player is authoritative for its own volume
        context.Player.Volume = volume / 100.0f;

        // 3. Inform MA of our volume (command + state echo)
        if (IsPlayerInActiveState(context.State))
        {
            FireAndForget(async () =>
            {
                try
                {
                    // Prevent feedback loop with PlayerStateChanged handler
                    context.IsUpdatingFromServer = true;
                    // Command MA to update its displayed volume
                    await context.Client.SetVolumeAsync(volume);
                    // SDK 5.4.0 auto-acknowledges, but we still echo state for full sync
                    await context.Client.SendPlayerStateAsync(volume, context.Player.IsMuted);
                    _logger.LogInformation("VOLUME [Sync] Player '{Name}': sent {Volume}% to MA",
                        name, volume);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync volume for '{Name}'", name);
                }
                finally
                {
                    context.IsUpdatingFromServer = false;
                }
            }, $"Volume sync for '{name}'", _logger);
        }

        // 4. Broadcast status update to all clients
        _ = BroadcastStatusAsync();

        // 5. Persist volume to config so it survives restarts
        _config.UpdatePlayerField(name, cfg => cfg.Volume = volume, save: true);

        return Task.FromResult(true);
    }


    /// <summary>
    /// Sets the mute state for a player.
    /// Applies software mute to the audio pipeline (not the hardware sink).
    /// Also syncs mute state to Music Assistant server.
    /// </summary>
    public bool SetMuted(string name, bool muted)
    {
        if (!_players.TryGetValue(name, out var context))
            return false;

        _logger.LogInformation("MUTE [UserToggle] Player '{Name}': {State}",
            name, muted ? "muted" : "unmuted");

        context.Pipeline.SetMuted(muted);
        context.Player.IsMuted = muted;

        // Sync mute state to Music Assistant server (bidirectional sync)
        FireAndForget(async () =>
        {
            try
            {
                // Prevent feedback loop with PlayerStateChanged handler
                context.IsUpdatingFromServer = true;
                await context.Client.SendPlayerStateAsync(context.Config.Volume, muted);
                context.LastConfirmedMuted = muted;
                _logger.LogInformation("MUTE [StateEcho] Player '{Name}': synced {State} to server",
                    name, muted ? "muted" : "unmuted");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync mute state for '{Name}'", name);
            }
            finally
            {
                context.IsUpdatingFromServer = false;
            }
        }, $"Mute state sync for '{name}'", _logger);

        return true;
    }

    /// <summary>
    /// Sets the delay offset for a player.
    /// This adjusts the playback timing to synchronize with other players.
    /// Positive values delay playback (play later), negative values advance it (play earlier).
    /// </summary>
    /// <param name="name">Player name.</param>
    /// <param name="delayMs">Delay offset in milliseconds (-5000 to 5000).</param>
    /// <returns>True if successful, false if player not found.</returns>
    public bool SetDelayOffset(string name, int delayMs)
    {
        if (!_players.TryGetValue(name, out var context))
            return false;

        // Clamp to valid range
        delayMs = Math.Clamp(delayMs, -5000, 5000);

        // Apply to the clock synchronizer
        context.ClockSync.StaticDelayMs = delayMs;
        context.Config.DelayMs = delayMs;

        if (delayMs != 0)
        {
            _logger.LogInformation("Set delay offset for '{Name}': {DelayMs}ms", name, delayMs);
        }

        // Broadcast status update so UI reflects the change
        _ = BroadcastStatusAsync();

        return true;
    }

    /// <summary>
    /// Starts a stopped player by recreating its connection.
    /// </summary>
    public async Task<PlayerResponse?> StartPlayerAsync(string name, CancellationToken ct = default)
    {
        if (!_players.TryGetValue(name, out var context))
            return null;

        // If already running, just return current state
        if (context.State == Models.PlayerState.Playing || context.State == Models.PlayerState.Connected ||
            context.State == Models.PlayerState.Buffering || context.State == Models.PlayerState.Connecting ||
            context.State == Models.PlayerState.Starting)
        {
            _logger.LogDebug("Player '{Name}' is already running (state={State})", name, context.State);
            return CreateResponse(name, context);
        }

        _logger.LogInformation("Starting player '{Name}'", name);

        // Use restart to recreate the player with fresh connections
        return await RestartPlayerAsync(name, ct);
    }

    /// <summary>
    /// Stops a player but keeps it in the dictionary for restart.
    /// The player will show as "Stopped" in the UI.
    /// </summary>
    public async Task<bool> StopPlayerAsync(string name)
    {
        if (_disposed)
            return false;

        if (!_players.TryGetValue(name, out var context))
            return false;

        // Mark as user-stopped to prevent auto-reconnection
        if (_pendingReconnections.TryGetValue(name, out var reconnectState))
        {
            _pendingReconnections[name] = reconnectState with { WasUserStopped = true };
        }

        // Already stopped?
        if (context.State == Models.PlayerState.Stopped)
        {
            _logger.LogDebug("Player '{Name}' is already stopped", name);
            return true;
        }

        _logger.LogInformation("Stopping player '{Name}' (user-initiated)", name);

        try
        {
            context.Cts.Cancel();
            context.State = Models.PlayerState.Stopped;

            // Stop pipeline and disconnect, but don't remove from dictionary
            try
            {
                await context.Pipeline.StopAsync().WaitAsync(DisposalTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timeout stopping pipeline for player '{Name}'", name);
            }

            try
            {
                await context.Client.DisconnectAsync("user_request").WaitAsync(DisposalTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timeout disconnecting player '{Name}'", name);
            }

            _logger.LogInformation("Player '{Name}' stopped (config preserved)", name);

            // Broadcast status update to all clients
            _ = BroadcastStatusAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping player '{Name}'", name);
            context.State = Models.PlayerState.Error;
            context.ErrorMessage = ex.Message;

            _ = BroadcastStatusAsync();

            return true; // Still consider it stopped
        }
    }

    /// <summary>
    /// Removes a player from the dictionary and disposes all resources.
    /// </summary>
    private async Task<bool> RemoveAndDisposePlayerAsync(string name)
    {
        if (_disposed)
            return false;

        if (!_players.TryRemove(name, out var context))
            return false;

        _logger.LogInformation("Removing and disposing player '{Name}'", name);

        try
        {
            // Unsubscribe event handlers FIRST to prevent:
            // 1. Memory leaks from handler closures holding context references
            // 2. Events firing during disposal that access disposed objects
            UnwireEvents(context);

            context.Cts.Cancel();

            await context.Client.DisconnectAsync("user_request").WaitAsync(DisposalTimeout);
            await context.Pipeline.StopAsync().WaitAsync(DisposalTimeout);
            await DisposePlayerContextAsync(context);

            _logger.LogInformation("Player '{Name}' removed and disposed", name);

            // Broadcast status update to all clients
            _ = BroadcastStatusAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing player '{Name}'", name);

            // Still broadcast since player was removed from dictionary
            _ = BroadcastStatusAsync();

            return true;
        }
    }

    /// <summary>
    /// Deletes a player completely (stops, removes from runtime, and removes from config).
    /// </summary>
    public async Task<bool> DeletePlayerAsync(string name)
    {
        if (_disposed)
            return false;

        // Remove from reconnection queue first
        RemoveFromReconnectionQueue(name);

        var removed = await RemoveAndDisposePlayerAsync(name);

        // Also remove from configuration
        if (_config.DeletePlayer(name))
        {
            _config.Save();
            _logger.LogInformation("Deleted player configuration: {Name}", name);
        }

        return removed;
    }

    /// <summary>
    /// Restarts a player (disposes old resources and recreates with same config).
    /// </summary>
    public async Task<PlayerResponse?> RestartPlayerAsync(string name, CancellationToken ct = default)
    {
        if (_disposed)
            return null;

        if (!_players.TryGetValue(name, out var context))
            return null;

        var config = context.Config;

        // Get startup volume from persisted config (NOT runtime volume)
        // This ensures MA learns the correct preference on reconnection
        var startupVolume = _config.Players.TryGetValue(name, out var persistedConfig)
            ? persistedConfig.Volume ?? 100
            : config.Volume;

        // Get advertised format from persisted config
        var advertisedFormat = persistedConfig?.AdvertisedFormat ?? config.AdvertisedFormat;

        // Fully remove and dispose old player
        await RemoveAndDisposePlayerAsync(name);

        var request = new PlayerCreateRequest
        {
            Name = config.Name,
            Device = config.DeviceId,
            ClientId = config.ClientId,
            ServerUrl = config.ServerUrl,
            Volume = startupVolume,  // Use startup volume, not runtime volume
            DelayMs = config.DelayMs,
            AdvertisedFormat = advertisedFormat,
            Persist = false // Already persisted
        };

        return await CreatePlayerAsync(request, ct);
    }

    /// <summary>
    /// Pauses playback for a player.
    /// </summary>
    /// <param name="name">The name of the player to pause.</param>
    /// <returns>True if the player was found and paused, false if not found.</returns>
    public bool PausePlayer(string name)
    {
        if (_players.TryGetValue(name, out var context))
        {
            context.Player.Pause();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resumes playback for a player.
    /// </summary>
    /// <param name="name">The name of the player to resume.</param>
    /// <returns>True if the player was found and resumed, false if not found.</returns>
    public bool ResumePlayer(string name)
    {
        if (_players.TryGetValue(name, out var context))
        {
            context.Player.Play();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Renames a player to a new name.
    /// Updates both the runtime state and persisted configuration.
    /// </summary>
    /// <param name="currentName">The current name of the player.</param>
    /// <param name="newName">The new name for the player.</param>
    /// <returns>True if the rename was successful, false if the player was not found.</returns>
    public bool RenamePlayer(string currentName, string newName)
    {
        // Validate new name (can happen outside lock)
        if (!ValidatePlayerName(newName, out var nameError))
        {
            throw new ArgumentException(nameError, nameof(newName));
        }

        // Early return for same name (no lock needed)
        if (currentName == newName)
        {
            return _players.ContainsKey(currentName);
        }

        // Early disposed check
        if (_disposed)
        {
            return false;
        }

        // Lock for the entire rename operation to make it atomic
        // This prevents race conditions where another thread creates a player
        // with newName between our check and the add
        lock (_playersLock)
        {
            // Re-check disposed inside lock
            if (_disposed)
            {
                return false;
            }

            // Check if new name already exists
            if (_players.ContainsKey(newName))
            {
                throw new EntityAlreadyExistsException("Player", newName);
            }

            // Get the player context
            if (!_players.TryGetValue(currentName, out var context))
            {
                return false;
            }

            _logger.LogInformation("Renaming player '{OldName}' to '{NewName}'", currentName, newName);

            // Atomic rename: remove old and add new within lock
            if (!_players.TryRemove(currentName, out _))
            {
                return false;
            }

            // Update the config name
            context.Config.Name = newName;

            // Add with new name - this will succeed since we verified
            // newName doesn't exist and we hold the lock
            _players[newName] = context;
        }

        // Config update and broadcast can happen outside lock (I/O operations)
        if (_config.RenamePlayer(currentName, newName))
        {
            _config.Save();
            _logger.LogDebug("Persisted rename for player '{OldName}' -> '{NewName}'", currentName, newName);
        }

        _ = BroadcastStatusAsync();

        _logger.LogInformation("Player renamed from '{OldName}' to '{NewName}'", currentName, newName);
        return true;
    }

    /// <summary>
    /// Wrapper method for ConnectPlayerAsync that ensures all exceptions are caught and logged.
    /// This prevents fire-and-forget tasks from losing exceptions.
    /// </summary>
    private async Task ConnectPlayerWithErrorHandlingAsync(string name, PlayerContext context, CancellationToken ct)
    {
        try
        {
            await ConnectPlayerAsync(name, context, ct);
        }
        catch (Exception ex)
        {
            // This should not normally be reached since ConnectPlayerAsync has its own error handling,
            // but we catch here as a safety net to ensure no exceptions are lost
            _logger.LogError(ex, "Unhandled exception during player '{Name}' connection", name);
            context.State = Models.PlayerState.Error;
            context.ErrorMessage = ex.Message;
            _ = BroadcastStatusAsync();
        }
    }

    /// <summary>
    /// Gets a cached server URI or performs mDNS discovery.
    /// Uses caching and locking to avoid race conditions when multiple players
    /// are created simultaneously.
    /// </summary>
    private async Task<Uri> GetOrDiscoverServerAsync(CancellationToken ct)
    {
        // Quick check without lock
        if (_cachedServerUri != null && DateTime.UtcNow < _cachedServerUriExpiry)
        {
            _logger.LogDebug("Using cached server URI: {Uri}", _cachedServerUri);
            return _cachedServerUri;
        }

        // Acquire lock to serialize discovery requests
        await _discoveryLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock (another thread may have discovered)
            if (_cachedServerUri != null && DateTime.UtcNow < _cachedServerUriExpiry)
            {
                _logger.LogDebug("Using cached server URI (after lock): {Uri}", _cachedServerUri);
                return _cachedServerUri;
            }

            _logger.LogInformation("Discovering Sendspin servers via mDNS...");

            // Defensive timeout wrapper - ensures we don't hang indefinitely even if
            // the SDK's ScanAsync doesn't properly respect the timeout parameter
            var scanTask = _serverDiscovery.ScanAsync(MdnsDiscoveryTimeout, ct);
            var timeoutTask = Task.Delay(MdnsDiscoveryTimeout.Add(TimeSpan.FromSeconds(1)), ct);

            var completedTask = await Task.WhenAny(scanTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                throw new TimeoutException($"mDNS server discovery timed out after {MdnsDiscoveryTimeout.TotalSeconds + 1} seconds");
            }

            var servers = await scanTask;
            var server = servers.FirstOrDefault()
                ?? throw new InvalidOperationException("No Sendspin servers found via mDNS discovery");

            // Construct WebSocket URI from discovered server
            var host = server.IpAddresses.FirstOrDefault() ?? server.Host;
            _cachedServerUri = new Uri($"ws://{host}:{server.Port}/sendspin");
            _cachedServerUriExpiry = DateTime.UtcNow.Add(CachedServerTtl);

            _logger.LogInformation("Found server: {ServerName} at {Uri}", server.Name, _cachedServerUri);
            return _cachedServerUri;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private async Task ConnectPlayerAsync(string name, PlayerContext context, CancellationToken ct)
    {
        try
        {
            context.State = Models.PlayerState.Starting;
            _ = BroadcastStatusAsync();

            // Discover server if URL not provided
            Uri? serverUri = null;
            if (!string.IsNullOrEmpty(context.Config.ServerUrl))
            {
                serverUri = new Uri(context.Config.ServerUrl);
            }
            else
            {
                // Use cached discovery to avoid race conditions when creating multiple players
                serverUri = await GetOrDiscoverServerAsync(ct);
            }

            // Connect
            context.State = Models.PlayerState.Connecting;
            _ = BroadcastStatusAsync();

            await context.Client.ConnectAsync(serverUri!, ct);

            context.State = Models.PlayerState.Connected;
            context.ConnectedAt = DateTime.UtcNow;

            // Capture server info (one-time, not in audio hot path)
            context.ConnectedAddress = $"{serverUri!.Host}:{serverUri.Port}";
            context.ServerName = context.Client.ServerName;

            _ = BroadcastStatusAsync();

            _logger.LogInformation("Player '{Name}' connected to server '{ServerName}' at {Address}",
                name, context.ServerName ?? "unknown", context.ConnectedAddress);

            // Push our configured volume to the server (overrides SDK's default volume:100)
            await PushVolumeToServerAsync(name, context);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Player '{Name}' connection cancelled", name);
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException or InvalidOperationException)
            {
                _logger.LogWarning("Player '{Name}' could not find server: {Message}", name, ex.Message);
            }
            else
            {
                _logger.LogWarning("Player '{Name}' failed to connect: {Message}", name, ex.Message);
            }
            _logger.LogDebug(ex, "Player '{Name}' connection failure details", name);

            context.State = Models.PlayerState.Error;
            context.ErrorMessage = ex.Message;
            _ = BroadcastStatusAsync();

            // If this is an initial connection failure (not managed by TryReconnectPlayerAsync),
            // queue for reconnection so the player doesn't stay in Error state forever.
            // TryReconnectPlayerAsync has its own re-queuing logic, so skip if already pending.
            if (!_pendingReconnections.ContainsKey(name))
            {
                var persistedConfig = _config.Players.TryGetValue(name, out var cfg) ? cfg : null;
                if (persistedConfig != null)
                {
                    // Players without a configured server URL rely on mDNS discovery —
                    // any failure should wait passively for mDNS rather than active retries
                    var isMdnsFailure = string.IsNullOrEmpty(persistedConfig.Server);

                    if (isMdnsFailure)
                    {
                        _logger.LogInformation(
                            "Player '{Name}' waiting for server discovery via mDNS", name);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Player '{Name}' initial connection failed, queuing for reconnection", name);
                    }

                    await RemoveAndDisposePlayerAsync(name);
                    QueueForReconnection(persistedConfig, mdnsOnly: isMdnsFailure);
                }
            }
        }
    }

    /// <summary>
    /// Wires all event handlers for a player context.
    /// </summary>
    /// <remarks>
    /// Creates and subscribes 5 event handlers:
    /// <list type="bullet">
    /// <item>ConnectionState - tracks connection state and triggers reconnection</item>
    /// <item>PipelineState - tracks playback state (Playing/Buffering/Idle)</item>
    /// <item>PipelineError - handles pipeline errors with auto-stop</item>
    /// <item>PlayerError - handles audio errors with auto-stop</item>
    /// <item>GroupState - logs group average volume (display only, SDK 5.4.0)</item>
    /// <item>PlayerState - handles server volume/mute commands with grace period (SDK 5.4.0)</item>
    /// </list>
    /// </remarks>
    private void WireEvents(string name, PlayerContext context)
    {
        // Create handler references for proper cleanup (prevents memory leaks)
        context.ConnectionStateHandler = CreateConnectionStateHandler(name, context);
        context.PipelineStateHandler = CreatePipelineStateHandler(name, context);
        context.PipelineErrorHandler = CreatePipelineErrorHandler(name, context);
        context.PlayerErrorHandler = CreatePlayerErrorHandler(name, context);
        context.GroupStateHandler = CreateGroupStateHandler(name, context);
        // SDK 5.4.0: PlayerStateChanged for individual player volume/mute commands
        context.PlayerStateHandler = CreatePlayerStateHandler(name, context);

        // Subscribe to events
        context.Client.ConnectionStateChanged += context.ConnectionStateHandler;
        context.Pipeline.StateChanged += context.PipelineStateHandler;
        context.Pipeline.ErrorOccurred += context.PipelineErrorHandler;
        context.Player.ErrorOccurred += context.PlayerErrorHandler;
        context.Client.GroupStateChanged += context.GroupStateHandler;
        // SDK 5.4.0: Subscribe to PlayerStateChanged
        context.Client.PlayerStateChanged += context.PlayerStateHandler;
    }

    /// <summary>
    /// Creates handler for connection state changes (Connected/Disconnected).
    /// Manages state transitions, grace period initialization, and reconnection queueing.
    /// </summary>
    private EventHandler<ConnectionStateChangedEventArgs> CreateConnectionStateHandler(
        string name, PlayerContext context)
    {
        return (_, args) =>
        {
            _logger.LogDebug("Player '{Name}' connection state: {State}", name, args.NewState);

            var previousState = context.State;

            context.State = args.NewState switch
            {
                ConnectionState.Connected => Models.PlayerState.Connected,
                ConnectionState.Connecting => Models.PlayerState.Connecting,
                ConnectionState.Handshaking => Models.PlayerState.Connecting,
                ConnectionState.Reconnecting => Models.PlayerState.Reconnecting,
                ConnectionState.Disconnected => Models.PlayerState.Stopped,
                ConnectionState.Disconnecting => context.State, // Brief transition, keep current
                _ => context.State
            };

            // Record connection time and push startup volume to server when connected
            // This ensures MA shows the correct startup volume immediately
            if (args.NewState == ConnectionState.Connected)
            {
                context.ConnectedAt = DateTime.UtcNow;
                _logger.LogInformation("VOLUME [GracePeriod] Player '{Name}': grace period started for {Duration}s",
                    name, VolumeGracePeriod.TotalSeconds);
                _ = PushVolumeToServerAsync(name, context);
            }

            // Handle disconnection - queue for reconnection if appropriate
            if (args.NewState == ConnectionState.Disconnected &&
                previousState != Models.PlayerState.Stopped &&  // Not user-stopped
                previousState != Models.PlayerState.Error &&    // Not already errored
                !_pendingReconnections.ContainsKey(name))        // Not already pending reconnection
            {
                _logger.LogWarning("Player '{Name}' disconnected unexpectedly, queuing for reconnection", name);

                // Get the persisted configuration for reconnection
                var persistedConfig = _config.Players.TryGetValue(name, out var cfg) ? cfg : null;
                if (persistedConfig == null)
                {
                    _logger.LogWarning("Player '{Name}' has no persisted configuration, cannot auto-reconnect", name);
                    return;
                }

                // Fire and forget the reconnection setup (can't await in event handler)
                FireAndForget(async () =>
                {
                    // Small delay to let disposal complete
                    await Task.Delay(100);

                    // Remove the disconnected player and queue for reconnection
                    await RemoveAndDisposePlayerAsync(name);
                    QueueForReconnection(persistedConfig);
                }, $"Reconnection setup for '{name}'", _logger);
            }

            // Broadcast status update on connection state change
            _ = BroadcastStatusAsync();
        };
    }

    /// <summary>
    /// Creates handler for pipeline state changes (Playing/Buffering/Idle).
    /// Updates player state and pushes volume on playback start.
    /// </summary>
    private EventHandler<AudioPipelineState> CreatePipelineStateHandler(
        string name, PlayerContext context)
    {
        return (_, state) =>
        {
            _logger.LogDebug("Player '{Name}' pipeline state: {State}", name, state);

            var previousState = context.State;

            var stateStr = state.ToString();

            if (state == AudioPipelineState.Playing)
                context.State = Models.PlayerState.Playing;
            else if (state == AudioPipelineState.Buffering)
                context.State = Models.PlayerState.Buffering;
            else if (state == AudioPipelineState.Idle)
                context.State = Models.PlayerState.Connected;
            else if (stateStr is "Starting")
            {
                context.State = Models.PlayerState.Starting;
                _logger.LogInformation("Player '{Name}' pipeline is starting", name);
            }
            else if (stateStr is "Stopping")
            {
                context.State = Models.PlayerState.Connected;
                _logger.LogInformation("Player '{Name}' pipeline is stopping", name);
            }
            else if (stateStr is "Dropping" or "Disconnecting")
                _logger.LogInformation("Player '{Name}' pipeline state: {State}", name, stateStr);
            else if (stateStr is "Inserting")
                _logger.LogWarning("Player '{Name}' pipeline is inserting frames (possible sync issue)", name);
            else
            {
                // Truly unknown state — log as warning so we notice SDK changes
                _logger.LogWarning("Player '{Name}' received unknown pipeline state: {State}", name, stateStr);
            }

            // Notify trigger service of playback state changes
            // Activate when transitioning TO an active state (Playing/Buffering) from inactive
            // Deactivate when transitioning TO a stopped state (Idle/Stopping) from active
            var isActiveState = state == AudioPipelineState.Playing || state == AudioPipelineState.Buffering;
            var wasInactiveState = previousState != Models.PlayerState.Playing && previousState != Models.PlayerState.Buffering;
            var isStoppedState = state == AudioPipelineState.Idle || stateStr is "Stopping";
            var wasActiveState = previousState == Models.PlayerState.Playing || previousState == Models.PlayerState.Buffering;

            if (isActiveState && wasInactiveState)
            {
                _triggerService.OnPlayerStarted(name, context.Config.DeviceId);
            }
            else if (isStoppedState && wasActiveState)
            {
                _triggerService.OnPlayerStopped(name, context.Config.DeviceId);
            }

            // Broadcast status update on pipeline state change
            _ = BroadcastStatusAsync();
        };
    }

    /// <summary>
    /// Creates handler for pipeline errors (decoder failures, etc.).
    /// Auto-stops the player to prevent resource waste.
    /// </summary>
    private EventHandler<AudioPipelineError> CreatePipelineErrorHandler(
        string name, PlayerContext context)
    {
        return (_, error) =>
        {
            _logger.LogError(error.Exception, "Player '{Name}' pipeline error: {Message}",
                name, error.Message);
            context.ErrorMessage = error.Message;

            // Auto-stop player on pipeline error to prevent resource waste
            _logger.LogWarning("Auto-stopping player '{Name}' due to pipeline error", name);
            FireAndForget(
                StopPlayerInternalAsync(name, "Pipeline error: " + error.Message),
                $"StopPlayerInternalAsync for '{name}' (pipeline error)", _logger);
        };
    }

    /// <summary>
    /// Creates handler for audio player errors (device unavailable, etc.).
    /// Auto-stops the player to prevent resource waste.
    /// </summary>
    private EventHandler<AudioPlayerError> CreatePlayerErrorHandler(
        string name, PlayerContext context)
    {
        return (_, error) =>
        {
            _logger.LogError(error.Exception, "Player '{Name}' audio error: {Message}",
                name, error.Message);
            context.ErrorMessage = error.Message;

            // Auto-stop player on audio error (e.g., device unavailable)
            _logger.LogWarning("Auto-stopping player '{Name}' due to audio error", name);
            FireAndForget(
                StopPlayerInternalAsync(name, "Audio error: " + error.Message),
                $"StopPlayerInternalAsync for '{name}' (audio error)", _logger);
        };
    }

    /// <summary>
    /// Handles GroupState events from the SDK.
    /// GroupState contains group-level data (average volume, muted, metadata).
    /// This is for logging/diagnostics — individual player commands come via PlayerStateChanged.
    /// </summary>
    private EventHandler<GroupState> CreateGroupStateHandler(
        string name, PlayerContext context)
    {
        return (_, group) =>
        {
            _logger.LogDebug(
                "GROUPSTATE Player '{Name}': GroupId={GroupId} vol={GroupVol}% muted={GroupMuted} (local vol={LocalVol}%)",
                name, group.GroupId, group.Volume, group.Muted, context.Config.Volume);
        };
    }

    /// <summary>
    /// Creates handler for server player-specific volume/mute commands (from Music Assistant UI, etc.).
    /// This handler receives commands directed at this specific player, not group averages.
    /// Implements grace period logic to preserve startup volume for 5 seconds after connection.
    /// </summary>
    /// <remarks>
    /// SDK 5.4.0 change: PlayerStateChanged is for individual player commands.
    /// GroupStateChanged now represents the average volume displayed to controllers.
    /// SDK automatically acknowledges by sending client/state when applying commands.
    /// </remarks>
    private EventHandler<SdkPlayerState> CreatePlayerStateHandler(
        string name, PlayerContext context)
    {
        return (_, playerState) =>
        {
            // Prevent feedback loops when we initiated the change
            if (context.IsUpdatingFromServer)
            {
                _logger.LogDebug(
                    "PLAYERSTATE Player '{Name}': ignoring echo (IsUpdatingFromServer=true)",
                    name);
                return;
            }

            _logger.LogInformation(
                "PLAYERSTATE Player '{Name}': received vol={ServerVol}% muted={ServerMuted} (local vol={LocalVol}% muted={LocalMuted})",
                name, playerState.Volume, playerState.Muted, context.Config.Volume, context.Player.IsMuted);

            // Clamp volume to valid range
            var serverVolume = Math.Clamp(playerState.Volume, 0, 100);

            // Check if we're within the grace period after connection
            var isWithinGracePeriod = context.ConnectedAt.HasValue &&
                                     (DateTime.UtcNow - context.ConnectedAt.Value) < VolumeGracePeriod;

            // Handle volume changes
            if (serverVolume != context.Config.Volume)
            {
                if (isWithinGracePeriod)
                {
                    var gracePeriodRemaining = VolumeGracePeriod - (DateTime.UtcNow - context.ConnectedAt!.Value);
                    _logger.LogInformation(
                        "VOLUME [GracePeriod] Player '{Name}': ignoring MA volume {NewVol}% (within grace period, keeping startup volume {OldVol}%, {Remaining:F1}s remaining)",
                        name, serverVolume, context.Config.Volume, gracePeriodRemaining.TotalSeconds);

                    // Push our startup volume back to MA aggressively
                    FireAndForget(async () =>
                    {
                        try
                        {
                            context.IsUpdatingFromServer = true;
                            await context.Client.SetVolumeAsync(context.Config.Volume);
                            _logger.LogInformation("VOLUME [GracePeriod] Player '{Name}': pushed startup volume {Volume}% back to MA",
                                name, context.Config.Volume);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to push startup volume for '{Name}'", name);
                        }
                        finally
                        {
                            context.IsUpdatingFromServer = false;
                        }
                    }, $"Grace period volume push for '{name}'", _logger);

                    return; // Don't update local volume or broadcast
                }

                // Outside grace period - accept MA's volume updates normally
                _logger.LogInformation("VOLUME [ServerSync] Player '{Name}': {OldVol}% -> {NewVol}%",
                    name, context.Config.Volume, serverVolume);

                // Update runtime config
                context.Config.Volume = serverVolume;

                // Apply volume locally - player is authoritative for its own volume
                context.Player.Volume = serverVolume / 100.0f;

                // Note: SDK 5.4.0 auto-acknowledges by sending client/state,
                // so we don't need to call SendPlayerStateAsync manually

                // Broadcast to UI so slider updates
                _ = BroadcastStatusAsync();

                // Persist volume to config so it survives restarts
                _config.UpdatePlayerField(name, cfg => cfg.Volume = serverVolume, save: true);
            }

            // Handle mute state from server
            if (playerState.Muted != context.Player.IsMuted)
            {
                _logger.LogInformation("MUTE [ServerSync] Player '{Name}': {OldState} -> {NewState}",
                    name,
                    context.Player.IsMuted ? "muted" : "unmuted",
                    playerState.Muted ? "muted" : "unmuted");

                // Update local state
                context.Pipeline.SetMuted(playerState.Muted);
                context.Player.IsMuted = playerState.Muted;
                context.LastConfirmedMuted = playerState.Muted;

                _ = BroadcastStatusAsync();
            }
        };
    }

    /// <summary>
    /// Unsubscribes all event handlers from a player context.
    /// Must be called before disposal to prevent memory leaks and access to disposed objects.
    /// </summary>
    private void UnwireEvents(PlayerContext context)
    {
        // Unsubscribe in reverse order of subscription
        // SDK 5.4.0: Unsubscribe PlayerStateChanged first (subscribed last)
        if (context.PlayerStateHandler != null)
        {
            context.Client.PlayerStateChanged -= context.PlayerStateHandler;
            context.PlayerStateHandler = null;
        }

        if (context.GroupStateHandler != null)
        {
            context.Client.GroupStateChanged -= context.GroupStateHandler;
            context.GroupStateHandler = null;
        }

        if (context.PlayerErrorHandler != null)
        {
            context.Player.ErrorOccurred -= context.PlayerErrorHandler;
            context.PlayerErrorHandler = null;
        }

        if (context.PipelineErrorHandler != null)
        {
            context.Pipeline.ErrorOccurred -= context.PipelineErrorHandler;
            context.PipelineErrorHandler = null;
        }

        if (context.PipelineStateHandler != null)
        {
            context.Pipeline.StateChanged -= context.PipelineStateHandler;
            context.PipelineStateHandler = null;
        }

        if (context.ConnectionStateHandler != null)
        {
            context.Client.ConnectionStateChanged -= context.ConnectionStateHandler;
            context.ConnectionStateHandler = null;
        }
    }

    private PlayerResponse CreateResponse(string name, PlayerContext context)
    {
        var bufferStats = context.Pipeline.BufferStats;

        // Check if player is pending reconnection
        var isPendingReconnection = _pendingReconnections.TryGetValue(name, out var reconnectState);

        // During reconnection, override transitional/error states so the UI doesn't flicker
        var displayState = context.State;
        if (isPendingReconnection && !reconnectState!.WasUserStopped &&
            displayState is Models.PlayerState.Starting or Models.PlayerState.Connecting
                        or Models.PlayerState.Created or Models.PlayerState.Error
                        or Models.PlayerState.Stopped)
        {
            displayState = reconnectState.MdnsOnly
                ? Models.PlayerState.WaitingForServer
                : Models.PlayerState.Reconnecting;
        }

        // Get startup volume from persisted config (not runtime config which changes with MA)
        var startupVolume = _config.Players.TryGetValue(name, out var persistedConfig)
            ? persistedConfig.Volume ?? 100
            : context.Config.Volume;

        return new PlayerResponse(
            Name: name,
            State: displayState,
            Device: context.Config.DeviceId,
            ClientId: context.Capabilities.ClientId,
            ServerUrl: context.Config.ServerUrl,
            ServerName: context.ServerName,
            ConnectedAddress: context.ConnectedAddress,
            Volume: context.Config.Volume, // Runtime volume (synced with MA)
            StartupVolume: startupVolume,   // Startup volume (from persisted config)
            IsMuted: context.Player.IsMuted,
            DelayMs: context.Config.DelayMs,
            OutputLatencyMs: context.Player.OutputLatencyMs,
            CreatedAt: context.CreatedAt,
            ConnectedAt: context.ConnectedAt,
            ErrorMessage: displayState is Models.PlayerState.Reconnecting or Models.PlayerState.WaitingForServer
                ? null : context.ErrorMessage,
            IsClockSynced: context.Client.IsClockSynced,
            Metrics: bufferStats != null ? new PlayerMetrics(
                BufferLevel: (int)bufferStats.BufferedMs,
                BufferCapacity: (int)bufferStats.TargetMs,
                SamplesPlayed: context.SamplesPlayed,
                Underruns: bufferStats.UnderrunCount,
                Overruns: bufferStats.OverrunCount
            ) : null,
            DeviceCapabilities: context.DeviceCapabilities,
            IsPendingReconnection: isPendingReconnection,
            ReconnectionAttempts: isPendingReconnection ? reconnectState!.RetryCount : null,
            NextReconnectionAttempt: isPendingReconnection ? reconnectState!.NextRetryTime : null,
            AdvertisedFormat: context.Config.AdvertisedFormat,
            CurrentTrack: GetTrackInfo(context.Client.CurrentGroup?.Metadata)
        );
    }

    /// <summary>
    /// Converts SDK TrackMetadata to our TrackInfo model.
    /// </summary>
    private static TrackInfo? GetTrackInfo(Sendspin.SDK.Models.TrackMetadata? metadata)
    {
        if (metadata == null || string.IsNullOrEmpty(metadata.Title))
            return null;

        return new TrackInfo(
            Title: metadata.Title,
            Artist: metadata.Artist,
            Album: metadata.Album,
            ArtworkUrl: metadata.ArtworkUrl,
            DurationSeconds: metadata.Duration,
            PositionSeconds: metadata.Position
        );
    }

    /// <summary>
    /// Gets real-time stats for a player (Stats for Nerds).
    /// </summary>
    /// <param name="name">Player name.</param>
    /// <returns>Stats response or null if player not found.</returns>
    public PlayerStatsResponse? GetPlayerStats(string name)
    {
        if (!_players.TryGetValue(name, out var context))
            return null;

        // Use cached device info (captured at player creation)
        // This avoids running pactl every time stats are requested
        return PlayerStatsMapper.BuildStats(
            name,
            context.Pipeline,
            context.ClockSync,
            context.Player,
            context.CachedDevice);
    }

    private static string GenerateClientId(string name)
    {
        // Use the ClientIdGenerator utility for consistent MD5-based IDs
        return ClientIdGenerator.Generate(name);
    }

    private static List<AudioFormat> GetDefaultFormats()
    {
        // Advertise hi-res formats to SendSpin/Music Assistant server
        // Server will send highest quality available that we support
        return new List<AudioFormat>
        {
            // Hi-res FLAC (preferred for quality)
            new AudioFormat { Codec = "flac", SampleRate = 192000, Channels = 2 },
            new AudioFormat { Codec = "flac", SampleRate = 96000, Channels = 2 },
            new AudioFormat { Codec = "flac", SampleRate = 48000, Channels = 2 },
            new AudioFormat { Codec = "flac", SampleRate = 44100, Channels = 2 },
            // Hi-res PCM
            new AudioFormat { Codec = "pcm", SampleRate = 192000, Channels = 2, BitDepth = 32 },
            new AudioFormat { Codec = "pcm", SampleRate = 96000, Channels = 2, BitDepth = 32 },
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 32 },
            new AudioFormat { Codec = "pcm", SampleRate = 192000, Channels = 2, BitDepth = 24 },
            new AudioFormat { Codec = "pcm", SampleRate = 96000, Channels = 2, BitDepth = 24 },
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 24 },
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 },
            new AudioFormat { Codec = "pcm", SampleRate = 44100, Channels = 2, BitDepth = 16 },
            // Opus for efficiency when streaming
            new AudioFormat { Codec = "opus", SampleRate = 48000, Channels = 2, Bitrate = 256 },
        };
    }

    /// <summary>
    /// Filters advertised audio formats based on user preference.
    /// Defaults to flac-48000 for maximum MA compatibility when no format specified.
    /// </summary>
    /// <param name="allFormats">List of all supported formats.</param>
    /// <param name="advertisedFormat">Format preference string (e.g., "flac-192000", "pcm-96000-24"). If null/empty, defaults to "flac-48000".</param>
    /// <returns>Filtered list containing only the preferred format, or all formats if preference is "all".</returns>
    private List<AudioFormat> FilterFormatsByPreference(List<AudioFormat> allFormats, string? advertisedFormat)
    {
        // Default to flac-48000 for maximum compatibility with all MA builds
        if (string.IsNullOrWhiteSpace(advertisedFormat))
        {
            advertisedFormat = "flac-48000";
        }

        // If explicitly set to "all", return all formats
        if (advertisedFormat.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return allFormats;
        }

        // Parse format string (e.g., "flac-192000" or "pcm-96000-24")
        var parts = advertisedFormat.Split('-');
        if (parts.Length < 2)
        {
            _logger.LogWarning("Invalid advertised format '{Format}', using all formats", advertisedFormat);
            return allFormats;
        }

        var codec = parts[0].ToLowerInvariant();
        if (!int.TryParse(parts[1], out var sampleRate))
        {
            _logger.LogWarning("Invalid sample rate in format '{Format}', using all formats", advertisedFormat);
            return allFormats;
        }

        int? bitDepth = null;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedBitDepth))
        {
            bitDepth = parsedBitDepth;
        }

        // Find matching format
        var matchingFormat = allFormats.FirstOrDefault(f =>
            f.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase) &&
            f.SampleRate == sampleRate &&
            (bitDepth == null || f.BitDepth == bitDepth));

        if (matchingFormat != null)
        {
            _logger.LogInformation(
                "Advertising single format: {Codec} {SampleRate}Hz{BitDepth}",
                matchingFormat.Codec,
                matchingFormat.SampleRate,
                matchingFormat.BitDepth.HasValue ? $" {matchingFormat.BitDepth}-bit" : "");
            return new List<AudioFormat> { matchingFormat };
        }

        _logger.LogWarning("Format '{Format}' not found, using all formats", advertisedFormat);
        return allFormats;
    }

    /// <summary>
    /// Internal method to stop a player due to an error, preserving it for restart.
    /// Unlike StopPlayerAsync, this keeps the player in the dictionary so it can be restarted.
    /// </summary>
    /// <param name="name">The player name.</param>
    /// <param name="reason">The reason for stopping (error message).</param>
    private async Task StopPlayerInternalAsync(string name, string reason)
    {
        if (!_players.TryGetValue(name, out var context))
            return;

        // Prevent re-entrancy if already stopping
        if (context.State == Models.PlayerState.Error || context.State == Models.PlayerState.Stopped)
        {
            _logger.LogDebug("Player '{Name}' already in {State} state, skipping internal stop", name, context.State);
            return;
        }

        _logger.LogInformation("Internal stop for player '{Name}': {Reason}", name, reason);

        // Notify trigger service that player stopped
        _triggerService.OnPlayerStopped(name, context.Config.DeviceId);

        try
        {
            // Update state to error first
            context.State = Models.PlayerState.Error;
            context.ErrorMessage = reason;

            // Stop the pipeline gracefully
            try
            {
                await context.Pipeline.StopAsync().WaitAsync(DisposalTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timeout stopping pipeline for player '{Name}'", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping pipeline for player '{Name}'", name);
            }

            // Disconnect from server gracefully
            try
            {
                await context.Client.DisconnectAsync("restart").WaitAsync(DisposalTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timeout disconnecting player '{Name}'", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting player '{Name}'", name);
            }

            _logger.LogInformation("Player '{Name}' stopped due to error. Use restart to reconnect.", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during internal stop of player '{Name}'", name);
        }
        finally
        {
            // Always broadcast status update
            _ = BroadcastStatusAsync();
        }
    }

    /// <summary>
    /// Broadcasts the current player status to all connected SignalR clients.
    /// </summary>
    private async Task BroadcastStatusAsync()
    {
        try
        {
            var players = GetAllPlayers();
            await _hubContext.BroadcastStatusUpdateAsync(players);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast player status update");
        }
    }

    #region Reconnection Methods

    /// <summary>
    /// Queues a player for automatic reconnection.
    /// </summary>
    private void QueueForReconnection(PlayerConfiguration config, bool mdnsOnly = false)
    {
        if (_disposed)
            return;

        var state = _pendingReconnections.GetOrAdd(config.Name, _ => new ReconnectionState(config));

        if (mdnsOnly)
        {
            // mDNS-only mode: no polling retries, just wait passively for mDNS watch.
            // NextRetryTime set to MaxValue so the polling loop never picks this up.
            _pendingReconnections[config.Name] = state with
            {
                RetryCount = 0,
                NextRetryTime = DateTime.MaxValue,
                MdnsOnly = true
            };

            _logger.LogInformation(
                "Player '{Name}' waiting for server discovery via mDNS (no active retries)",
                config.Name);
        }
        else
        {
            // Normal reconnection: exponential backoff polling + mDNS watch
            var delay = CalculateBackoffDelay(state.RetryCount);
            var nextRetry = DateTime.UtcNow.Add(delay);

            _pendingReconnections[config.Name] = state with
            {
                RetryCount = state.RetryCount + 1,
                NextRetryTime = nextRetry,
                MdnsOnly = false
            };

            _logger.LogInformation(
                "Player '{Name}' queued for reconnection (attempt {Attempt}, next retry in {Delay:F0}s)",
                config.Name, state.RetryCount + 1, delay.TotalSeconds);
        }

        // Start mDNS watch to detect server reappearance immediately
        StartMdnsWatch();

        // Broadcast status so UI shows reconnection state
        _ = BroadcastStatusAsync();
    }

    /// <summary>
    /// Removes a player from the reconnection queue.
    /// </summary>
    private void RemoveFromReconnectionQueue(string name)
    {
        if (_pendingReconnections.TryRemove(name, out _))
        {
            _logger.LogDebug("Player '{Name}' removed from reconnection queue", name);

            // Stop mDNS watch if no more players are pending
            if (_pendingReconnections.IsEmpty)
            {
                StopMdnsWatch();
            }
        }
    }

    /// <summary>
    /// Starts continuous mDNS discovery to detect when a server reappears.
    /// When a server is found, all pending players' backoff timers are reset
    /// to trigger immediate reconnection.
    /// </summary>
    private void StartMdnsWatch()
    {
        if (_mdnsWatchActive || _disposed)
            return;

        _mdnsWatchActive = true;
        // Subscribe to both Found (new server) and Updated (returning server already in cache)
        _serverDiscovery.ServerFound += OnMdnsServerFound;
        _serverDiscovery.ServerUpdated += OnMdnsServerFound;
        _serverDiscovery.StartAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception, "Failed to start mDNS watch");
            else
                _logger.LogInformation("mDNS watch started - listening for server reappearance");
        });
    }

    /// <summary>
    /// Stops continuous mDNS discovery when no players are pending reconnection.
    /// </summary>
    private void StopMdnsWatch()
    {
        if (!_mdnsWatchActive)
            return;

        _mdnsWatchActive = false;
        _serverDiscovery.ServerFound -= OnMdnsServerFound;
        _serverDiscovery.ServerUpdated -= OnMdnsServerFound;
        _serverDiscovery.StopAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception, "Failed to stop mDNS watch");
            else
                _logger.LogDebug("mDNS watch stopped");
        });
    }

    /// <summary>
    /// Called when mDNS discovers a server while players are pending reconnection.
    /// Resets all pending players' backoff timers to trigger immediate reconnection.
    /// </summary>
    private void OnMdnsServerFound(object? sender, DiscoveredServer server)
    {
        var pendingCount = _pendingReconnections.Count;
        if (pendingCount == 0)
            return;

        _logger.LogInformation(
            "mDNS discovered server '{Name}' at {Host}:{Port} - triggering immediate reconnection for {Count} player(s)",
            server.Name, server.Host, server.Port, pendingCount);

        // Stop watching now that we've found the server.
        // If reconnection fails, QueueForReconnection will restart the watch.
        StopMdnsWatch();

        // Cache the discovered server URI so reconnection attempts use it directly
        var host = server.IpAddresses.FirstOrDefault() ?? server.Host;
        _cachedServerUri = new Uri($"ws://{host}:{server.Port}/sendspin");
        _cachedServerUriExpiry = DateTime.UtcNow.Add(CachedServerTtl);

        // Reset all pending players' backoff timers to now and clear mDNS-only mode.
        // After mDNS discovery, if connection still fails, normal backoff retries kick in.
        foreach (var kvp in _pendingReconnections)
        {
            _pendingReconnections[kvp.Key] = kvp.Value with
            {
                NextRetryTime = DateTime.UtcNow,
                MdnsOnly = false
            };
        }

        // Wake the reconnection loop immediately instead of waiting for the 1s poll
        SignalReconnectionLoop();

        _ = BroadcastStatusAsync();
    }

    /// <summary>
    /// Calculates exponential backoff delay for reconnection attempts.
    /// </summary>
    private static TimeSpan CalculateBackoffDelay(int retryCount)
    {
        // Exponential backoff: 5s, 10s, 20s, 40s, 80s, 120s (max)
        var delaySeconds = InitialReconnectDelay.TotalSeconds * Math.Pow(2, retryCount);
        var cappedSeconds = Math.Min(delaySeconds, MaxReconnectDelay.TotalSeconds);
        return TimeSpan.FromSeconds(cappedSeconds);
    }

    /// <summary>
    /// Wakes the reconnection loop to process pending players immediately.
    /// Called by OnMdnsServerFound when a server is discovered.
    /// </summary>
    private void SignalReconnectionLoop()
    {
        // Release the semaphore if it's not already signaled (max count is 1)
        try { _reconnectionSignal.Release(); }
        catch (SemaphoreFullException) { /* Already signaled */ }
    }

    /// <summary>
    /// Background task that processes pending reconnection attempts.
    /// Wakes on 1-second polling interval OR immediately when signaled (e.g. mDNS discovery).
    /// </summary>
    private async Task ProcessReconnectionsAsync(CancellationToken ct)
    {
        _logger.LogDebug("Reconnection background task started");

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                // Wait up to 1 second, but wake immediately if signaled
                try { await _reconnectionSignal.WaitAsync(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }

                var now = DateTime.UtcNow;
                var playersToReconnect = _pendingReconnections
                    .Where(kvp => kvp.Value.NextRetryTime <= now && !kvp.Value.WasUserStopped)
                    .Select(kvp => (kvp.Key, kvp.Value))
                    .ToList();

                if (playersToReconnect.Count == 0)
                    continue;

                // Reconnect all ready players concurrently
                var tasks = new List<Task>();
                foreach (var (name, state) in playersToReconnect)
                {
                    if (ct.IsCancellationRequested || _disposed)
                        break;

                    // Check if max attempts reached (if configured)
                    if (MaxReconnectAttempts > 0 && state.RetryCount >= MaxReconnectAttempts)
                    {
                        _logger.LogWarning(
                            "Player '{Name}' exceeded max reconnection attempts ({Max}), giving up",
                            name, MaxReconnectAttempts);
                        _pendingReconnections.TryRemove(name, out _);
                        continue;
                    }

                    tasks.Add(TryReconnectPlayerAsync(name, state, ct));
                }

                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in reconnection background task");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        _logger.LogDebug("Reconnection background task stopped");
    }

    /// <summary>
    /// Attempts to reconnect a single player.
    /// </summary>
    private async Task TryReconnectPlayerAsync(string name, ReconnectionState state, CancellationToken ct)
    {
        _logger.LogInformation("Attempting to reconnect player '{Name}' (attempt {Attempt})",
            name, state.RetryCount);

        try
        {
            // Check if player exists and remove it first (cleanup failed state)
            if (_players.ContainsKey(name))
            {
                _logger.LogDebug("Removing existing failed player '{Name}' before reconnection attempt", name);
                await RemoveAndDisposePlayerAsync(name);
            }

            // Only invalidate cached server URI if the mDNS watch hasn't found a server.
            // If the watch already found one, use that cached URI for fast reconnection.
            if (!_mdnsWatchActive || _cachedServerUri == null)
            {
                _cachedServerUri = null;
            }

            var request = new PlayerCreateRequest
            {
                Name = state.Config.Name,
                Device = state.Config.PortAudioDeviceIndex?.ToString() ?? state.Config.Device,
                ClientId = ClientIdGenerator.Generate(state.Config.Name),
                ServerUrl = state.Config.Server,
                Volume = state.Config.Volume ?? 100,
                DelayMs = state.Config.DelayMs,
                AdvertisedFormat = state.Config.AdvertisedFormat,
                Persist = false // Already persisted
            };

            await CreatePlayerAsync(request, ct);

            // CreatePlayerAsync returns before the connection completes (it's fire-and-forget).
            // Wait for the actual connection to succeed or fail before deciding.
            var connected = await WaitForPlayerConnectionAsync(name, TimeSpan.FromSeconds(20), ct);

            if (connected)
            {
                _pendingReconnections.TryRemove(name, out _);
                _logger.LogInformation("Player '{Name}' reconnected successfully after {Attempts} attempt(s)",
                    name, state.RetryCount);
            }
            else
            {
                throw new InvalidOperationException("Connection did not succeed within timeout");
            }
        }
        catch (EntityAlreadyExistsException)
        {
            // Player was created by another path, remove from queue
            _pendingReconnections.TryRemove(name, out _);
            _logger.LogDebug("Player '{Name}' already exists, removing from reconnection queue", name);
        }
        catch (ArgumentException ex)
        {
            // Device validation failed - stop reconnecting, let user fix config
            _pendingReconnections.TryRemove(name, out _);
            _logger.LogError(ex,
                "Reconnection stopped for player '{Name}': {Message}. " +
                "Player will remain in error state until manually fixed.",
                name, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Reconnection attempt {Attempt} failed for player '{Name}': {Message}",
                state.RetryCount, name, ex.Message);
            _logger.LogDebug(ex, "Player '{Name}' reconnection failure details", name);

            // Remove the failed player so the UI shows "Reconnecting (attempt N)"
            // instead of "Error: ..." during the backoff period. Without this cleanup,
            // the player sits in _players with Error state and the API shows the live
            // player state instead of the reconnection queue state.
            if (_players.ContainsKey(name))
                await RemoveAndDisposePlayerAsync(name);

            // Queue next attempt with incremented retry count
            QueueForReconnection(state.Config);
        }
    }

    /// <summary>
    /// Waits for a player's background connection to reach a terminal state.
    /// CreatePlayerAsync starts the connection via fire-and-forget, so we need
    /// to poll the player state to know if it actually connected.
    /// </summary>
    private async Task<bool> WaitForPlayerConnectionAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);

            if (!_players.TryGetValue(name, out var ctx))
                return false; // Player was removed (disposed during connection)

            if (ctx.State is Models.PlayerState.Connected or Models.PlayerState.Playing or Models.PlayerState.Buffering)
                return true;

            if (ctx.State is Models.PlayerState.Error or Models.PlayerState.Stopped)
                return false;
        }

        return false;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Synchronously disposes of all managed resources.
    /// WARNING: This method blocks until all resources are disposed or timeout occurs.
    /// Prefer using DisposeAsync when possible to avoid potential deadlocks.
    /// </summary>
    public void Dispose()
    {
        List<PlayerContext> contextsToDispose;

        lock (_playersLock)
        {
            if (_disposed)
                return;
            _disposed = true;

            // Stop reconnection task
            _reconnectionCts?.Cancel();
            _pendingReconnections.Clear();

            // Take snapshot and clear while holding lock
            // This prevents race conditions with concurrent player operations
            contextsToDispose = _players.Values.ToList();
            _players.Clear();
        }

        // Stop mDNS watch outside lock
        StopMdnsWatch();

        // Dispose outside lock to avoid potential deadlocks
        foreach (var context in contextsToDispose)
        {
            // Unsubscribe event handlers first to prevent memory leaks and disposal crashes
            UnwireEvents(context);
            context.Cts.Cancel();
            try
            {
                // Use Task.Run to avoid sync context deadlocks when calling async dispose methods
                // Note: .Wait() is used here because Dispose() must be synchronous per IDisposable contract
                Task.Run(() => DisposePlayerContextAsync(context)).Wait(DisposalTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing player context");
            }
        }

        _reconnectionSignal.Dispose();
        _logger.LogInformation("PlayerManagerService disposed");
    }

    /// <summary>
    /// Asynchronously disposes of all managed resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<PlayerContext> contextsToDispose;

        lock (_playersLock)
        {
            if (_disposed)
                return;
            _disposed = true;

            // Stop reconnection task
            _reconnectionCts?.Cancel();
            _pendingReconnections.Clear();

            // Take snapshot and clear while holding lock
            // This prevents race conditions with concurrent player operations
            contextsToDispose = _players.Values.ToList();
            _players.Clear();
        }

        // Stop mDNS watch outside lock
        StopMdnsWatch();

        // Dispose outside lock to avoid potential deadlocks
        foreach (var context in contextsToDispose)
        {
            // Unsubscribe event handlers first to prevent memory leaks and disposal crashes
            UnwireEvents(context);
            context.Cts.Cancel();
            try
            {
                await DisposePlayerContextAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing player context");
            }
        }

        _reconnectionSignal.Dispose();
        _logger.LogInformation("PlayerManagerService disposed asynchronously");
    }

    #endregion
}
