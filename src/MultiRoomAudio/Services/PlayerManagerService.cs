using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using MultiRoomAudio.Audio;
using MultiRoomAudio.Hubs;
using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;

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
        IHubContext<PlayerStatusHub> hubContext)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        _environment = environment;
        _hubContext = hubContext;
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

        var tasks = playerNames.Select(name => StopPlayerAsync(name)).ToArray();
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
            var decoderFactory = new AudioDecoderFactory();
            var pipeline = new AudioPipeline(
                _loggerFactory.CreateLogger<AudioPipeline>(),
                decoderFactory,
                clockSync,
                bufferFactory: (format, sync) => new TimedAudioBuffer(format, sync, bufferCapacityMs: AudioBufferCapacityMs),
                playerFactory: () => player,
                sourceFactory: (buffer, timeFunc) => new BufferedAudioSampleSource(buffer, timeFunc));

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

            // 11. Persist configuration if requested
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

            // 12. Start connection in background with proper error handling
            _ = ConnectPlayerWithErrorHandlingAsync(request.Name, context, ct);

            // 13. Broadcast status update to all clients
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
    /// </summary>
    public async Task<bool> SetVolumeAsync(string name, int volume, CancellationToken ct = default)
    {
        if (!_players.TryGetValue(name, out var context))
            return false;

        volume = Math.Clamp(volume, 0, 100);
        _logger.LogDebug("Setting volume for '{Name}' to {Volume}", name, volume);

        try
        {
            await context.Client.SetVolumeAsync(volume);
            context.Config.Volume = volume;

            // Broadcast status update to all clients
            _ = BroadcastStatusAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set volume for player '{Name}'", name);
            throw;
        }
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
    /// Stops and removes a player.
    /// </summary>
    public async Task<bool> StopPlayerAsync(string name)
    {
        if (!_players.TryRemove(name, out var context))
            return false;

        _logger.LogInformation("Stopping player '{Name}'", name);

        try
        {
            context.Cts.Cancel();

            await context.Client.DisconnectAsync("Player stopped");
            await context.Pipeline.StopAsync();
            await context.Client.DisposeAsync();
            await context.Pipeline.DisposeAsync();
            await context.Player.DisposeAsync();
            await context.Connection.DisposeAsync();

            _logger.LogInformation("Player '{Name}' stopped and disposed", name);

            // Broadcast status update to all clients
            _ = BroadcastStatusAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping player '{Name}'", name);

            // Still broadcast since player was removed from dictionary
            _ = BroadcastStatusAsync();

            return true; // Still consider it stopped
        }
    }

    /// <summary>
    /// Deletes a player completely (stops, removes from runtime, and removes from config).
    /// </summary>
    public async Task<bool> DeletePlayerAsync(string name)
    {
        var stopped = await StopPlayerAsync(name);

        // Also remove from configuration
        if (_config.DeletePlayer(name))
        {
            _config.Save();
            _logger.LogInformation("Deleted player configuration: {Name}", name);
        }

        return stopped;
    }

    /// <summary>
    /// Restarts a player (stops and recreates with same config).
    /// </summary>
    public async Task<PlayerResponse?> RestartPlayerAsync(string name, CancellationToken ct = default)
    {
        if (!_players.TryGetValue(name, out var context))
            return null;

        var config = context.Config;

        await StopPlayerAsync(name);

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

            // Broadcast status update on error
            _ = BroadcastStatusAsync();
        };

        context.Player.ErrorOccurred += (_, error) =>
        {
            _logger.LogError(error.Exception, "Player '{Name}' audio error: {Message}",
                name, error.Message);
            context.ErrorMessage = error.Message;

            // Broadcast status update on error
            _ = BroadcastStatusAsync();
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
        if (_disposed) return;
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
        if (_disposed) return;
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
