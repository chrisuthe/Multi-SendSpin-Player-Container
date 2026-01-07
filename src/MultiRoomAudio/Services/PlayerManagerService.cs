using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Audio;
using MultiRoomAudio.Hubs;
using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages the lifecycle of all Sendspin players.
/// Handles creation, connection, state management, and disposal.
/// Integrates with ConfigurationService for persistence and autostart.
/// </summary>
public class PlayerManagerService : IHostedService, IAsyncDisposable, IDisposable
{
    private readonly ILogger<PlayerManagerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConfigurationService _config;
    private readonly EnvironmentService _environment;
    private readonly IHubContext<PlayerStatusHub> _hubContext;
    private readonly VolumeCommandRunner _volumeRunner;
    private readonly BackendFactory _backendFactory;
    private readonly ConcurrentDictionary<string, PlayerContext> _players = new();
    private readonly MdnsServerDiscovery _serverDiscovery;
    private bool _disposed;

    #region Constants

    /// <summary>
    /// Audio buffer capacity in bytes (32MB).
    /// </summary>
    private const int AudioBufferCapacityBytes = 32_000_000;

    /// <summary>
    /// Audio buffer capacity in milliseconds for timed audio buffer.
    /// </summary>
    private const int AudioBufferCapacityMs = 8000;

    /// <summary>
    /// Target buffer level in milliseconds for faster startup.
    /// Lower values = faster start but more sensitive to jitter.
    /// With SDK 3.0's HasMinimalSync, typical startup is 300-500ms.
    /// </summary>
    private const int TargetBufferMs = 250;

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
    };

    /// <summary>
    /// Timeout for mDNS server discovery.
    /// </summary>
    private static readonly TimeSpan MdnsDiscoveryTimeout = TimeSpan.FromSeconds(5);

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
    /// Allows alphanumeric characters, spaces, hyphens, and underscores.
    /// </summary>
    private static readonly Regex ValidPlayerNamePattern = new(
        @"^[a-zA-Z0-9\s\-_]+$",
        RegexOptions.Compiled);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Determines if a player is in an active state where it can receive commands.
    /// Active states include Starting, Connecting, Connected, Playing, and Buffering.
    /// </summary>
    /// <param name="state">The player state to check.</param>
    /// <returns>True if the player is in an active state.</returns>
    private static bool IsPlayerInActiveState(PlayerState state)
    {
        return state == PlayerState.Starting ||
               state == PlayerState.Connecting ||
               state == PlayerState.Connected ||
               state == PlayerState.Playing ||
               state == PlayerState.Buffering;
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
    /// Converts volume percentage (0-100) to normalized audio level (0.0-1.0)
    /// using squared scaling for perceived loudness (volume 50 sounds half as loud).
    /// </summary>
    /// <param name="volumePercent">Volume as integer percentage (0-100).</param>
    /// <returns>Normalized volume for audio player (0.0-1.0, squared).</returns>
    private static float NormalizeVolume(int volumePercent)
    {
        var normalized = Math.Clamp(volumePercent, 0, 100) / 100f;
        return normalized * normalized;
    }

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
        DeviceCapabilities? DeviceCapabilities = null
    )
    {
        public PlayerState State { get; set; } = PlayerState.Created;
        public string? ErrorMessage { get; set; }
        public DateTime? ConnectedAt { get; set; }
        public long SamplesPlayed { get; set; }
    }

    public PlayerManagerService(
        ILogger<PlayerManagerService> logger,
        ILoggerFactory loggerFactory,
        ConfigurationService config,
        EnvironmentService environment,
        IHubContext<PlayerStatusHub> hubContext,
        VolumeCommandRunner volumeRunner,
        BackendFactory backendFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        _environment = environment;
        _hubContext = hubContext;
        _volumeRunner = volumeRunner;
        _backendFactory = backendFactory;
        _serverDiscovery = new MdnsServerDiscovery(
            loggerFactory.CreateLogger<MdnsServerDiscovery>());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PlayerManagerService starting...");

        // Autostart configured players
        var autostartPlayers = _config.GetAutostartPlayers();
        if (autostartPlayers.Count > 0)
        {
            _logger.LogInformation("Found {AutostartCount} players configured for autostart",
                autostartPlayers.Count);

            foreach (var playerConfig in autostartPlayers)
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
                        Persist = false, // Already persisted, don't re-save
                        OutputSampleRate = playerConfig.OutputSampleRate,
                        OutputBitDepth = playerConfig.OutputBitDepth
                    };

                    await CreatePlayerAsync(request, cancellationToken);
                    _logger.LogInformation("Player {PlayerName} autostarted successfully", playerConfig.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to autostart player {PlayerName}. Device: {Device}, Server: {Server}",
                        playerConfig.Name,
                        playerConfig.Device ?? "(default)",
                        playerConfig.Server ?? "(auto-discover)");
                }
            }
        }
        else
        {
            _logger.LogInformation("No players configured for autostart");
        }

        _logger.LogInformation("PlayerManagerService started with {PlayerCount} active players",
            _players.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PlayerManagerService stopping with {PlayerCount} active players...",
            _players.Count);

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

        // Validate against allowed character pattern
        if (!ValidPlayerNamePattern.IsMatch(name))
        {
            errorMessage = "Player name contains invalid characters. Only alphanumeric characters, spaces, hyphens, and underscores are allowed.";
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

        if (_players.ContainsKey(request.Name))
        {
            throw new InvalidOperationException($"Player '{request.Name}' already exists");
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
            // Probe device capabilities (used for format selection and reporting)
            var deviceCapabilities = _backendFactory.GetDeviceCapabilities(request.Device);

            // Resolve output format: request > saved config > auto-detect > defaults
            // PulseAudio handles any necessary format conversion to the device
            AudioOutputFormat? outputFormat = null;
            if (request.OutputSampleRate.HasValue || request.OutputBitDepth.HasValue)
            {
                // Use explicitly specified values from request
                outputFormat = new AudioOutputFormat(
                    SampleRate: request.OutputSampleRate ?? 48000,
                    BitDepth: request.OutputBitDepth ?? 32,
                    Channels: 2);
                _logger.LogInformation("Using requested output format: {SampleRate}Hz/{BitDepth}-bit",
                    outputFormat.SampleRate, outputFormat.BitDepth);
            }
            else if (deviceCapabilities != null)
            {
                // Auto-detect from device capabilities
                outputFormat = new AudioOutputFormat(
                    SampleRate: deviceCapabilities.PreferredSampleRate,
                    BitDepth: deviceCapabilities.PreferredBitDepth,
                    Channels: 2);
                _logger.LogInformation("Auto-detected output format: {SampleRate}Hz/{BitDepth}-bit (device supports: rates={Rates}, depths={Depths})",
                    outputFormat.SampleRate, outputFormat.BitDepth,
                    string.Join(",", deviceCapabilities.SupportedSampleRates),
                    string.Join(",", deviceCapabilities.SupportedBitDepths));
            }
            else
            {
                // Fallback to default (48kHz/32-bit)
                _logger.LogDebug("No device capabilities available, using default output format");
            }

            // 1. Create capabilities with player role
            var clientCapabilities = new ClientCapabilities
            {
                ClientId = request.ClientId ?? GenerateClientId(request.Name),
                ClientName = request.Name,
                Roles = new List<string> { "player@v1" },
                AudioFormats = GetDefaultFormats(),
                BufferCapacity = AudioBufferCapacityBytes
            };

            // 2. Create clock synchronizer
            var clockSync = new KalmanClockSynchronizer(
                _loggerFactory.CreateLogger<KalmanClockSynchronizer>());

            // 3. Create audio player using the appropriate backend with output format
            var player = _backendFactory.CreatePlayer(request.Device, _loggerFactory, outputFormat);

            // 4. Create audio pipeline with proper factories
            // Uses direct passthrough - PulseAudio handles format conversion to devices
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
                        bufferCapacityMs: AudioBufferCapacityMs,
                        syncOptions: PulseAudioSyncOptions);
                    buffer.TargetBufferMilliseconds = TargetBufferMs;  // Faster startup (250ms vs default 500ms)
                    return buffer;
                },
                playerFactory: () => player,
                sourceFactory: (buffer, timeFunc) =>
                {
                    // Direct passthrough - no resampling
                    // PulseAudio handles format conversion to device natively
                    return new BufferedAudioSampleSource(buffer, timeFunc);
                },
                waitForConvergence: true,      // Wait for minimal sync (2 measurements) before playback
                convergenceTimeoutMs: 1000);   // 1 second timeout (SDK 3.0 uses HasMinimalSync for fast start)

            // 5. Create WebSocket connection
            var connection = new SendspinConnection(
                _loggerFactory.CreateLogger<SendspinConnection>());

            // 6. Create SDK client
            var client = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                connection,
                clockSync,
                clientCapabilities,
                pipeline);

            // 7. Create config for tracking
            var config = new PlayerConfig
            {
                Name = request.Name,
                DeviceId = request.Device,
                ClientId = clientCapabilities.ClientId,
                ServerUrl = request.ServerUrl,
                Volume = request.Volume,
                DelayMs = request.DelayMs,
                OutputSampleRate = outputFormat?.SampleRate,
                OutputBitDepth = outputFormat?.BitDepth,
                OutputFormat = outputFormat
            };

            // 8. Create context
            var cts = new CancellationTokenSource();
            var context = new PlayerContext(
                client, connection, pipeline, player, clockSync, clientCapabilities, config,
                DateTime.UtcNow, cts, deviceCapabilities)
            {
                State = PlayerState.Created
            };

            // 9. Wire up events
            WireEvents(request.Name, context);

            // 10. Store context
            _players[request.Name] = context;

            // 11. Apply initial volume (software volume scaling)
            player.Volume = NormalizeVolume(request.Volume);

            // 12. Apply delay offset from user configuration
            clockSync.StaticDelayMs = request.DelayMs;
            if (request.DelayMs != 0)
            {
                _logger.LogInformation("Delay offset for '{Name}': {DelayMs}ms", request.Name, request.DelayMs);
            }

            // 13. Persist configuration if requested
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
                    OutputSampleRate = outputFormat?.SampleRate,
                    OutputBitDepth = outputFormat?.BitDepth
                };
                _config.SetPlayer(request.Name, persistConfig);
                _config.Save();
                _logger.LogDebug("Persisted configuration for player '{Name}'", request.Name);
            }

            // 14. Start connection in background with proper error handling
            // Use the player's own cancellation token, not the request token,
            // so the connection persists after the HTTP response is sent
            _ = ConnectPlayerWithErrorHandlingAsync(request.Name, context, context.Cts.Token);

            // 15. Broadcast status update to all clients
            _ = BroadcastStatusAsync();

            return CreateResponse(request.Name, context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create player '{Name}'", request.Name);
            throw;
        }
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
    /// Gets all active players.
    /// </summary>
    public PlayersListResponse GetAllPlayers()
    {
        var players = _players
            .Select(kvp => CreateResponse(kvp.Key, kvp.Value))
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
    /// Updates local config, notifies server (if connected), and applies to audio output.
    /// </summary>
    public async Task<bool> SetVolumeAsync(string name, int volume, CancellationToken ct = default)
    {
        if (!_players.TryGetValue(name, out var context))
            return false;

        volume = Math.Clamp(volume, 0, 100);
        _logger.LogDebug("Setting volume for '{Name}' to {Volume}", name, volume);

        // 1. Update local config (always)
        context.Config.Volume = volume;

        // 2. Apply software volume to audio player
        context.Player.Volume = NormalizeVolume(volume);

        // 3. Notify server of volume change (only if in an active state)
        if (IsPlayerInActiveState(context.State))
        {
            try
            {
                await context.Client.SetVolumeAsync(volume);
            }
            catch (Exception ex)
            {
                // Don't fail the whole operation if server notification fails
                _logger.LogWarning(ex, "Failed to notify server of volume change for '{Name}'", name);
            }
        }

        // 4. Optionally set hardware/OS volume via PulseAudio pactl
        // Only if we have a specific device (not default)
        // Fire-and-forget with error handling via ContinueWith
        if (!string.IsNullOrEmpty(context.Config.DeviceId))
        {
            _ = _backendFactory.SetVolumeAsync(context.Config.DeviceId, volume, ct)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        _logger.LogDebug(t.Exception.InnerException ?? t.Exception,
                            "Hardware volume control not available for '{Name}'", name);
                    }
                }, TaskScheduler.Default);
        }

        // 5. Broadcast status update to all clients
        _ = BroadcastStatusAsync();

        return true;
    }

    /// <summary>
    /// Sets the mute state for a player.
    /// </summary>
    public bool SetMuted(string name, bool muted)
    {
        if (!_players.TryGetValue(name, out var context))
            return false;

        context.Pipeline.SetMuted(muted);
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
        if (context.State == PlayerState.Playing || context.State == PlayerState.Connected ||
            context.State == PlayerState.Buffering || context.State == PlayerState.Connecting ||
            context.State == PlayerState.Starting)
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
        if (!_players.TryGetValue(name, out var context))
            return false;

        // Already stopped?
        if (context.State == PlayerState.Stopped)
        {
            _logger.LogDebug("Player '{Name}' is already stopped", name);
            return true;
        }

        _logger.LogInformation("Stopping player '{Name}'", name);

        try
        {
            context.Cts.Cancel();
            context.State = PlayerState.Stopped;

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
                await context.Client.DisconnectAsync("Player stopped").WaitAsync(DisposalTimeout);
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
            context.State = PlayerState.Error;
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
        if (!_players.TryRemove(name, out var context))
            return false;

        _logger.LogInformation("Removing and disposing player '{Name}'", name);

        try
        {
            context.Cts.Cancel();

            await context.Client.DisconnectAsync("Player removed").WaitAsync(DisposalTimeout);
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
        if (!_players.TryGetValue(name, out var context))
            return null;

        var config = context.Config;

        // Fully remove and dispose old player
        await RemoveAndDisposePlayerAsync(name);

        var request = new PlayerCreateRequest
        {
            Name = config.Name,
            Device = config.DeviceId,
            ClientId = config.ClientId,
            ServerUrl = config.ServerUrl,
            Volume = config.Volume,
            DelayMs = config.DelayMs,
            Persist = false, // Already persisted
            OutputSampleRate = config.OutputSampleRate,
            OutputBitDepth = config.OutputBitDepth
        };

        return await CreatePlayerAsync(request, ct);
    }

    /// <summary>
    /// Pauses playback for a player.
    /// </summary>
    public void PausePlayer(string name)
    {
        if (_players.TryGetValue(name, out var context))
        {
            context.Player.Pause();
        }
    }

    /// <summary>
    /// Resumes playback for a player.
    /// </summary>
    public void ResumePlayer(string name)
    {
        if (_players.TryGetValue(name, out var context))
        {
            context.Player.Play();
        }
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
        // Validate new name
        if (!ValidatePlayerName(newName, out var nameError))
        {
            throw new ArgumentException(nameError, nameof(newName));
        }

        // Check if new name already exists (and it's not the same player)
        if (currentName != newName && _players.ContainsKey(newName))
        {
            throw new InvalidOperationException($"A player named '{newName}' already exists");
        }

        // Get the player context
        if (!_players.TryGetValue(currentName, out var context))
        {
            return false;
        }

        _logger.LogInformation("Renaming player '{OldName}' to '{NewName}'", currentName, newName);

        // If same name, nothing to do
        if (currentName == newName)
        {
            return true;
        }

        // Update the in-memory player dictionary
        if (!_players.TryRemove(currentName, out _))
        {
            return false;
        }

        // Update the config name
        context.Config.Name = newName;

        // Add with new name
        _players[newName] = context;

        // Update persisted configuration
        if (_config.RenamePlayer(currentName, newName))
        {
            _config.Save();
            _logger.LogDebug("Persisted rename for player '{OldName}' -> '{NewName}'", currentName, newName);
        }

        // Broadcast status update
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
            context.State = PlayerState.Error;
            context.ErrorMessage = ex.Message;
            _ = BroadcastStatusAsync();
        }
    }

    private async Task ConnectPlayerAsync(string name, PlayerContext context, CancellationToken ct)
    {
        try
        {
            context.State = PlayerState.Starting;
            _ = BroadcastStatusAsync();

            // Discover server if URL not provided
            Uri? serverUri = null;
            if (!string.IsNullOrEmpty(context.Config.ServerUrl))
            {
                serverUri = new Uri(context.Config.ServerUrl);
            }
            else
            {
                _logger.LogInformation("Discovering Sendspin servers via mDNS...");
                var servers = await _serverDiscovery.ScanAsync(
                    MdnsDiscoveryTimeout, ct);

                var server = servers.FirstOrDefault();
                if (server == null)
                {
                    throw new InvalidOperationException("No Sendspin servers found via mDNS discovery");
                }

                // Construct WebSocket URI from discovered server
                var host = server.IpAddresses.FirstOrDefault() ?? server.Host;
                serverUri = new Uri($"ws://{host}:{server.Port}/sendspin");
                _logger.LogInformation("Found server: {ServerName} at {Uri}",
                    server.Name, serverUri);
            }

            // Connect
            context.State = PlayerState.Connecting;
            _ = BroadcastStatusAsync();

            await context.Client.ConnectAsync(serverUri!, ct);

            context.State = PlayerState.Connected;
            context.ConnectedAt = DateTime.UtcNow;
            _ = BroadcastStatusAsync();

            _logger.LogInformation("Player '{Name}' connected to server", name);

            // Push our configured volume to the server (overrides server's stored value)
            try
            {
                await context.Client.SetVolumeAsync(context.Config.Volume);
                _logger.LogDebug("Player '{Name}' pushed volume {Volume} to server", name, context.Config.Volume);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push initial volume for player '{Name}'", name);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Player '{Name}' connection cancelled", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect player '{Name}'", name);
            context.State = PlayerState.Error;
            context.ErrorMessage = ex.Message;
            _ = BroadcastStatusAsync();
        }
    }

    private void WireEvents(string name, PlayerContext context)
    {
        context.Client.ConnectionStateChanged += (_, args) =>
        {
            _logger.LogDebug("Player '{Name}' connection state: {State}", name, args.NewState);

            context.State = args.NewState switch
            {
                ConnectionState.Connected => PlayerState.Connected,
                ConnectionState.Disconnected => PlayerState.Stopped,
                _ => context.State
            };

            // Broadcast status update on connection state change
            _ = BroadcastStatusAsync();
        };

        context.Pipeline.StateChanged += (_, state) =>
        {
            _logger.LogDebug("Player '{Name}' pipeline state: {State}", name, state);

            if (state == AudioPipelineState.Playing)
                context.State = PlayerState.Playing;
            else if (state == AudioPipelineState.Buffering)
                context.State = PlayerState.Buffering;

            // Broadcast status update on pipeline state change
            _ = BroadcastStatusAsync();
        };

        context.Pipeline.ErrorOccurred += (_, error) =>
        {
            _logger.LogError(error.Exception, "Player '{Name}' pipeline error: {Message}",
                name, error.Message);
            context.ErrorMessage = error.Message;

            // Auto-stop player on pipeline error to prevent resource waste
            _logger.LogWarning("Auto-stopping player '{Name}' due to pipeline error", name);
            FireAndForget(
                StopPlayerInternalAsync(name, "Pipeline error: " + error.Message),
                $"StopPlayerInternalAsync for '{name}' (pipeline error)");
        };

        context.Player.ErrorOccurred += (_, error) =>
        {
            _logger.LogError(error.Exception, "Player '{Name}' audio error: {Message}",
                name, error.Message);
            context.ErrorMessage = error.Message;

            // Auto-stop player on audio error (e.g., device unavailable)
            _logger.LogWarning("Auto-stopping player '{Name}' due to audio error", name);
            FireAndForget(
                StopPlayerInternalAsync(name, "Audio error: " + error.Message),
                $"StopPlayerInternalAsync for '{name}' (audio error)");
        };

        // Handle volume changes from server (Music Assistant UI, etc.)
        context.Client.GroupStateChanged += (_, group) =>
        {
            // Clamp volume to valid range
            var serverVolume = Math.Clamp(group.Volume, 0, 100);

            // Update volume if changed
            if (serverVolume != context.Config.Volume)
            {
                _logger.LogDebug("Player '{Name}' volume changed by server: {OldVol} -> {NewVol}",
                    name, context.Config.Volume, serverVolume);

                context.Config.Volume = serverVolume;

                // Only apply software volume if player is in an active state
                if (IsPlayerInActiveState(context.State))
                {
                    try
                    {
                        context.Player.Volume = NormalizeVolume(serverVolume);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to apply volume change from server for player '{Name}'", name);
                    }
                }

                // Broadcast to UI so slider updates
                _ = BroadcastStatusAsync();
            }
        };
    }

    private PlayerResponse CreateResponse(string name, PlayerContext context)
    {
        var bufferStats = context.Pipeline.BufferStats;

        return new PlayerResponse(
            Name: name,
            State: context.State,
            Device: context.Config.DeviceId,
            ClientId: context.Capabilities.ClientId,
            ServerUrl: context.Config.ServerUrl,
            Volume: context.Config.Volume,
            IsMuted: context.Player.IsMuted,
            DelayMs: context.Config.DelayMs,
            OutputLatencyMs: context.Player.OutputLatencyMs,
            CreatedAt: context.CreatedAt,
            ConnectedAt: context.ConnectedAt,
            ErrorMessage: context.ErrorMessage,
            IsClockSynced: context.Client.IsClockSynced,
            Metrics: bufferStats != null ? new PlayerMetrics(
                BufferLevel: (int)bufferStats.BufferedMs,
                BufferCapacity: (int)bufferStats.TargetMs,
                SamplesPlayed: context.SamplesPlayed,
                Underruns: bufferStats.UnderrunCount,
                Overruns: bufferStats.OverrunCount
            ) : null,
            OutputFormat: context.Config.OutputFormat,
            DeviceCapabilities: context.DeviceCapabilities
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

        var bufferStats = context.Pipeline.BufferStats;
        var clockStatus = context.ClockSync.GetStatus();
        var inputFormat = context.Pipeline.CurrentFormat;
        // Use SDK's OutputFormat if available (3.3.1+), fall back to config
        var pipelineOutputFormat = context.Pipeline.OutputFormat;
        var configOutputFormat = context.Config.OutputFormat;

        // Audio Format Stats
        var audioFormat = new AudioFormatStats(
            InputFormat: inputFormat != null
                ? $"{inputFormat.Codec.ToUpperInvariant()} {inputFormat.SampleRate}Hz {inputFormat.Channels}ch"
                : "--",
            InputSampleRate: inputFormat?.SampleRate ?? 0,
            InputChannels: inputFormat?.Channels ?? 0,
            InputBitrate: inputFormat?.Bitrate > 0 ? $"{inputFormat.Bitrate}kbps" : null,
            OutputFormat: pipelineOutputFormat != null
                ? $"PCM {pipelineOutputFormat.SampleRate}Hz {pipelineOutputFormat.Channels}ch {pipelineOutputFormat.BitDepth}-bit"
                : (configOutputFormat != null
                    ? $"PCM {configOutputFormat.SampleRate}Hz {configOutputFormat.Channels}ch {configOutputFormat.BitDepth}-bit"
                    : "--"),
            OutputSampleRate: pipelineOutputFormat?.SampleRate ?? configOutputFormat?.SampleRate ?? 0,
            OutputChannels: pipelineOutputFormat?.Channels ?? configOutputFormat?.Channels ?? 2,
            OutputBitDepth: pipelineOutputFormat?.BitDepth ?? configOutputFormat?.BitDepth ?? 32
        );

        // Sync Stats
        var sync = new SyncStats(
            SyncErrorMs: bufferStats?.SyncErrorMs ?? 0,
            CorrectionMode: bufferStats?.CurrentCorrectionMode.ToString() ?? "None",
            PlaybackRate: bufferStats?.TargetPlaybackRate ?? 1.0,
            IsPlaybackActive: bufferStats?.IsPlaybackActive ?? false
        );

        // Buffer Stats
        var buffer = new BufferStatsInfo(
            BufferedMs: (int)(bufferStats?.BufferedMs ?? 0),
            TargetMs: (int)(bufferStats?.TargetMs ?? 0),
            Underruns: bufferStats?.UnderrunCount ?? 0,
            Overruns: bufferStats?.OverrunCount ?? 0
        );

        // Clock Sync Stats
        var clockSync = new ClockSyncStats(
            IsSynchronized: clockStatus.IsConverged,
            ClockOffsetMs: clockStatus.OffsetMilliseconds,
            UncertaintyMs: clockStatus.OffsetUncertaintyMicroseconds / 1000.0,
            DriftRatePpm: clockStatus.DriftMicrosecondsPerSecond,
            IsDriftReliable: clockStatus.IsDriftReliable,
            MeasurementCount: clockStatus.MeasurementCount,
            OutputLatencyMs: context.Pipeline.DetectedOutputLatencyMs,
            StaticDelayMs: (int)context.ClockSync.StaticDelayMs
        );

        // Throughput Stats
        var throughput = new ThroughputStats(
            SamplesWritten: bufferStats?.TotalSamplesWritten ?? 0,
            SamplesRead: bufferStats?.TotalSamplesRead ?? 0,
            SamplesDroppedSync: bufferStats?.SamplesDroppedForSync ?? 0,
            SamplesInsertedSync: bufferStats?.SamplesInsertedForSync ?? 0,
            SamplesDroppedOverflow: bufferStats?.DroppedSamples ?? 0
        );

        // Format Conversion Stats (PulseAudio handles actual conversion)
        // Shows input vs output rates for informational purposes
        var inputRate = inputFormat?.SampleRate ?? 48000;
        var outputRate = pipelineOutputFormat?.SampleRate ?? configOutputFormat?.SampleRate ?? inputRate;
        var effectiveRatio = outputRate / (double)inputRate * (bufferStats?.TargetPlaybackRate ?? 1.0);

        var resampler = new ResamplerStats(
            InputRate: inputRate,
            OutputRate: outputRate,
            Quality: "PulseAudio", // PulseAudio handles format conversion
            EffectiveRatio: effectiveRatio
        );

        return new PlayerStatsResponse(
            PlayerName: name,
            AudioFormat: audioFormat,
            Sync: sync,
            Buffer: buffer,
            ClockSync: clockSync,
            Throughput: throughput,
            Resampler: resampler
        );
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
            // Hi-res PCM
            new AudioFormat { Codec = "pcm", SampleRate = 192000, Channels = 2, BitDepth = 24 },
            new AudioFormat { Codec = "pcm", SampleRate = 96000, Channels = 2, BitDepth = 24 },
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 },
            // Opus for efficiency when streaming
            new AudioFormat { Codec = "opus", SampleRate = 48000, Channels = 2, Bitrate = 256 },
        };
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
        if (context.State == PlayerState.Error || context.State == PlayerState.Stopped)
        {
            _logger.LogDebug("Player '{Name}' already in {State} state, skipping internal stop", name, context.State);
            return;
        }

        _logger.LogInformation("Internal stop for player '{Name}': {Reason}", name, reason);

        try
        {
            // Update state to error first
            context.State = PlayerState.Error;
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
                await context.Client.DisconnectAsync("Audio error: " + reason).WaitAsync(DisposalTimeout);
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

    /// <summary>
    /// Safely executes a fire-and-forget task, ensuring any exceptions are logged.
    /// Use this when discarding a Task to prevent unobserved exceptions.
    /// </summary>
    /// <param name="task">The task to execute.</param>
    /// <param name="context">Description of what the task is doing (for logging).</param>
    private void FireAndForget(Task task, string context)
    {
        task.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    _logger.LogError(t.Exception.InnerException ?? t.Exception,
                        "Unhandled exception in fire-and-forget task: {Context}", context);
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Synchronously disposes of all managed resources.
    /// WARNING: This method blocks until all resources are disposed or timeout occurs.
    /// Prefer using DisposeAsync when possible to avoid potential deadlocks.
    /// </summary>
    public void Dispose()
    {
        lock (_players)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        foreach (var context in _players.Values)
        {
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

        _players.Clear();

        // Note: MdnsServerDiscovery does not implement IDisposable
        // If it did, we would dispose it here

        _logger.LogInformation("PlayerManagerService disposed");
    }

    /// <summary>
    /// Asynchronously disposes of all managed resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_players)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        foreach (var context in _players.Values)
        {
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

        _players.Clear();

        // Note: MdnsServerDiscovery does not implement IDisposable or IAsyncDisposable
        // If it did, we would dispose it here

        _logger.LogInformation("PlayerManagerService disposed asynchronously");
    }
}
