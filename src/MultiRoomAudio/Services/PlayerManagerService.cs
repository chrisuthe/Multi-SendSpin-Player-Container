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
    private readonly AlsaCommandRunner _alsaRunner;
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
        CancellationTokenSource Cts
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
        AlsaCommandRunner alsaRunner)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        _environment = environment;
        _hubContext = hubContext;
        _alsaRunner = alsaRunner;
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
                        Persist = false // Already persisted, don't re-save
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
        if (!PortAudioDeviceEnumerator.ValidateDevice(request.Device, out var deviceError))
        {
            throw new ArgumentException(deviceError);
        }

        _logger.LogInformation("Creating player '{Name}' with device '{Device}'",
            request.Name, request.Device ?? "default");

        try
        {
            // 1. Create capabilities with player role
            var capabilities = new ClientCapabilities
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

            // 3. Create audio player (PortAudio for Linux)
            var player = new PortAudioPlayer(
                _loggerFactory.CreateLogger<PortAudioPlayer>(),
                request.Device);

            // 4. Create audio pipeline with proper factories
            // Uses resampling source for smooth sync correction (SDK 2.2.0 tiered correction)
            var decoderFactory = new AudioDecoderFactory();
            var pipeline = new AudioPipeline(
                _loggerFactory.CreateLogger<AudioPipeline>(),
                decoderFactory,
                clockSync,
                bufferFactory: (format, sync) =>
                {
                    var buffer = new TimedAudioBuffer(format, sync, bufferCapacityMs: AudioBufferCapacityMs);
                    buffer.TargetBufferMilliseconds = TargetBufferMs;  // Faster startup (250ms vs default 500ms)
                    return buffer;
                },
                playerFactory: () => player,
                sourceFactory: (buffer, timeFunc) =>
                {
                    var source = new BufferedAudioSampleSource(buffer, timeFunc);
                    // Wrap with resampling for smooth playback rate adjustment (±4%)
                    return new ResamplingAudioSampleSource(
                        source,
                        buffer,
                        _loggerFactory.CreateLogger<ResamplingAudioSampleSource>());
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
                capabilities,
                pipeline);

            // 7. Create config for tracking
            var config = new PlayerConfig
            {
                Name = request.Name,
                DeviceId = request.Device,
                ClientId = capabilities.ClientId,
                ServerUrl = request.ServerUrl,
                Volume = request.Volume,
                DelayMs = request.DelayMs
            };

            // 8. Create context
            var cts = new CancellationTokenSource();
            var context = new PlayerContext(
                client, connection, pipeline, player, clockSync, capabilities, config,
                DateTime.UtcNow, cts)
            {
                State = PlayerState.Created
            };

            // 9. Wire up events
            WireEvents(request.Name, context);

            // 10. Store context
            _players[request.Name] = context;

            // 11. Apply initial volume (software volume scaling)
            var normalizedVolume = request.Volume / 100f;
            player.Volume = normalizedVolume * normalizedVolume;

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
                    Volume = request.Volume
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
        if (!PortAudioDeviceEnumerator.ValidateDevice(newDeviceId, out var error))
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

        // 2. Apply software volume to audio player (0-100 -> 0.0-1.0)
        // Using squared scaling for perceived loudness (volume 50 sounds half as loud)
        var normalizedVolume = volume / 100f;
        context.Player.Volume = normalizedVolume * normalizedVolume;

        // 3. Notify server of volume change (only if connected)
        if (context.State == PlayerState.Connected || context.State == PlayerState.Playing || context.State == PlayerState.Buffering)
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

        // 4. Optionally set hardware/OS volume via ALSA/PulseAudio
        // Only if we have a specific device (not default)
        if (!string.IsNullOrEmpty(context.Config.DeviceId))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _alsaRunner.SetVolumeAsync(context.Config.DeviceId, volume, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Hardware volume control not available for '{Name}'", name);
                }
            }, ct);
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
            await context.Client.DisposeAsync();
            await context.Pipeline.DisposeAsync();
            await context.Player.DisposeAsync();
            await context.Connection.DisposeAsync();

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
            Persist = false // Already persisted
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
            _ = StopPlayerInternalAsync(name, "Pipeline error: " + error.Message);
        };

        context.Player.ErrorOccurred += (_, error) =>
        {
            _logger.LogError(error.Exception, "Player '{Name}' audio error: {Message}",
                name, error.Message);
            context.ErrorMessage = error.Message;

            // Auto-stop player on audio error (e.g., device unavailable)
            _logger.LogWarning("Auto-stopping player '{Name}' due to audio error", name);
            _ = StopPlayerInternalAsync(name, "Audio error: " + error.Message);
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

                // Only apply software volume if player is in a valid state
                if (context.State == PlayerState.Connected || context.State == PlayerState.Playing || context.State == PlayerState.Buffering)
                {
                    try
                    {
                        // Apply software volume (squared for perceived loudness)
                        var normalizedVolume = serverVolume / 100f;
                        context.Player.Volume = normalizedVolume * normalizedVolume;
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
            ) : null
        );
    }

    private static string GenerateClientId(string name)
    {
        // Use the ClientIdGenerator utility for consistent MD5-based IDs
        return ClientIdGenerator.Generate(name);
    }

    private static List<AudioFormat> GetDefaultFormats()
    {
        return new List<AudioFormat>
        {
            new AudioFormat { Codec = "opus", SampleRate = 48000, Channels = 2, Bitrate = 256 },
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 },
            new AudioFormat { Codec = "flac", SampleRate = 48000, Channels = 2 }
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var context in _players.Values)
        {
            context.Cts.Cancel();
            try
            {
                // Use Task.Run to avoid sync context deadlocks when calling async dispose methods
                Task.Run(async () =>
                {
                    await context.Client.DisposeAsync();
                    await context.Pipeline.DisposeAsync();
                    await context.Player.DisposeAsync();
                    await context.Connection.DisposeAsync();
                }).Wait(DisposalTimeout);
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
        if (_disposed)
            return;
        _disposed = true;

        foreach (var context in _players.Values)
        {
            context.Cts.Cancel();
            try
            {
                await context.Client.DisposeAsync();
                await context.Pipeline.DisposeAsync();
                await context.Player.DisposeAsync();
                await context.Connection.DisposeAsync();
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
