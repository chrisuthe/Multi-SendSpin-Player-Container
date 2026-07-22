using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using MultiRoomAudio.Mqtt;

namespace MultiRoomAudio.Services;

/// <summary>
/// Owns the MQTT broker connection and bridges Multi-Room Audio state and a
/// focused set of controls to Home Assistant via MQTT Discovery.
/// Runs as a StartupOrchestrator phase so any failure is non-blocking.
/// </summary>
public class MqttService
{
    private readonly MqttConfigService _config;
    private readonly PlayerManagerService _players;
    private readonly TriggerService _triggers;
    private readonly VersionService _version;
    private readonly EnvironmentService _env;
    private readonly StartupProgressService _startup;
    private readonly ILogger<MqttService> _logger;

    private IMqttClient? _client;
    private MqttTopics? _topics;
    private HaDiscovery? _discovery;
    private string _baseTopic = "multiroom-audio";
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly RetainedPublishCache _retained = new();
    private CancellationTokenSource? _reconnectCts;
    private volatile bool _shuttingDown;

    public MqttService(
        MqttConfigService config,
        PlayerManagerService players,
        TriggerService triggers,
        VersionService version,
        EnvironmentService env,
        StartupProgressService startup,
        ILogger<MqttService> logger)
    {
        _config = config;
        _players = players;
        _triggers = triggers;
        _version = version;
        _env = env;
        _startup = startup;
        _logger = logger;
    }

    /// <summary>Gets whether the MQTT client is currently connected to the broker.</summary>
    /// <summary>
    /// Builds a globally-unique MQTT client ID. The GUID suffix is essential:
    /// MQTT brokers allow only one connection per client ID, so a non-unique ID
    /// (e.g. a stale session from a prior start, a second add-on instance, or the
    /// shared host name under host_network) causes the broker to kick one client
    /// off as the other connects — an endless connect/disconnect takeover loop.
    /// </summary>
    internal static string NewClientId()
        => $"multiroom-audio-{Environment.MachineName}-{Guid.NewGuid():N}";

    public bool IsConnected => _client?.IsConnected ?? false;

    /// <summary>Gets the most recent connection or disconnect error message, or null if healthy.</summary>
    public string? LastError { get; private set; }

    /// <summary>Initializes the MQTT client, connects to the configured broker, and publishes discovery and state.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        _config.Reload();
        var settings = _config.Current;

        if (!settings.Enabled)
        {
            _logger.LogInformation("MQTT bridge disabled; skipping");
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            _logger.LogWarning("MQTT bridge enabled but no broker host configured; skipping");
            LastError = "No broker host configured";
            return;
        }

        _baseTopic = settings.BaseTopic;
        _topics = new MqttTopics(settings.BaseTopic, settings.DiscoveryPrefix);
        _discovery = new HaDiscovery(_topics, _version.Version);

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(NewClientId())
            .WithTcpServer(settings.Host, settings.Port)
            .WithWillTopic(_topics.BridgeAvailabilityTopic)
            .WithWillPayload(Encoding.UTF8.GetBytes("offline"))
            .WithWillRetain()
            .WithCleanSession();

        if (!string.IsNullOrEmpty(settings.Username))
            optionsBuilder.WithCredentials(settings.Username, settings.Password ?? string.Empty);
        if (settings.UseTls)
            optionsBuilder.WithTlsOptions(o => { });

        var options = optionsBuilder.Build();

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;

        _options = options;
        await ConnectAndAnnounceAsync(ct);

        _players.PlayersChanged += OnPlayersChanged;
        _triggers.TriggersChanged += OnTriggersChanged;
    }

    private MqttClientOptions? _options;

    private async Task ConnectAndAnnounceAsync(CancellationToken ct)
    {
        await _client!.ConnectAsync(_options!, ct);
        LastError = null;
        _logger.LogInformation("MQTT bridge connected to broker");

        // A broker that restarted may have lost its retained set, so forget what we think it
        // holds and let the announce below re-prime every topic in full.
        await _publishLock.WaitAsync(ct);
        try
        { _retained.Clear(); }
        finally { _publishLock.Release(); }

        await _client.SubscribeAsync(_topics!.PlayerCommandSubscription, MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await _client.SubscribeAsync(_topics!.AmpCommandSubscription, MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await PublishAvailabilityAsync("online", ct);
        await PublishDiscoveryAsync(ct);
        await PublishAllStateAsync(ct);
    }

    private async Task PublishAvailabilityAsync(string value, CancellationToken ct)
        => await PublishAsync(_topics!.BridgeAvailabilityTopic, value, retain: true, ct);

    private async Task PublishDiscoveryAsync(CancellationToken ct)
    {
        foreach (var p in _players.GetAllPlayers().Players)
            foreach (var m in _discovery!.ForPlayerDevice(p))
                await PublishAsync(m.Topic, m.Payload, retain: true, ct);

        foreach (var m in _discovery!.ForContainerDevice(_env.EnvironmentName))
            await PublishAsync(m.Topic, m.Payload, retain: true, ct);

        var trig = _triggers.GetStatus();
        foreach (var board in trig.Boards)
            foreach (var t in board.Triggers)
                foreach (var m in _discovery!.ForAmpDevice(board.BoardId, board.DisplayName, t))
                    await PublishAsync(m.Topic, m.Payload, retain: true, ct);
    }

    private async Task PublishAllStateAsync(CancellationToken ct)
    {
        var players = _players.GetAllPlayers().Players;
        foreach (var p in players)
            await PublishAsync(_topics!.PlayerStateTopic(p.ClientId), MqttStatePayloads.Player(p), retain: true, ct);

        await PublishAsync(_topics!.ContainerStateTopic,
            MqttStatePayloads.Container(_startup.IsStartupComplete, _version.Version,
                players.Count, _env.AudioBackend, _env.EnvironmentName),
            retain: true, ct);

        var trigState = _triggers.GetStatus();
        foreach (var board in trigState.Boards)
            foreach (var t in board.Triggers)
                await PublishAsync(_topics!.AmpStateTopic(board.BoardId, t.Channel),
                    MqttStatePayloads.Amp(t, board.IsConnected), retain: true, ct);
    }

    private void OnPlayersChanged()
    {
        // Fire-and-forget; serialize via the publish lock. Never throw into the caller.
        _ = Task.Run(async () =>
        {
            try
            {
                if (!IsConnected)
                    return;
                // Restored now that RetainedPublishCache suppresses identical republishes (#256):
                // this costs nothing when no config changed, and a newly added player still needs
                // its discovery config announced before its state topic means anything to HA.
                await PublishDiscoveryAsync(CancellationToken.None);
                await PublishAllStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish MQTT state on player change");
            }
        });
    }

    private void OnTriggersChanged()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!IsConnected)
                    return;
                await PublishDiscoveryAsync(CancellationToken.None);
                await PublishAllStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish MQTT state on trigger change");
            }
        });
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            // MQTTnet v5: Payload is ReadOnlySequence<byte>; flatten to byte array for UTF-8 decoding.
            var seq = e.ApplicationMessage.Payload;
            var payloadBytes = seq.IsSingleSegment
                ? seq.First.ToArray()
                : System.Buffers.BuffersExtensions.ToArray(seq);
            var payload = Encoding.UTF8.GetString(payloadBytes);
            var cmd = MqttCommand.Parse(_baseTopic, topic, payload);
            if (cmd.Kind == MqttCommandKind.Unknown)
                return;

            if (cmd.Kind == MqttCommandKind.AmpOverride && cmd.AmpZone is not null)
            {
                foreach (var board in _triggers.GetStatus().Boards)
                {
                    foreach (var t in board.Triggers)
                    {
                        if ($"{MqttTopics.Sanitize(board.BoardId)}_{t.Channel}" == cmd.AmpZone)
                        {
                            _triggers.SetOverride(board.BoardId, t.Channel, cmd.BoolValue ?? false);
                            return;
                        }
                    }
                }
                _logger.LogWarning("MQTT amp override for unknown zone {Zone}", cmd.AmpZone);
                return;
            }

            var player = _players.GetAllPlayers().Players
                .FirstOrDefault(p => MqttTopics.Sanitize(p.ClientId) == cmd.PlayerClientId);
            if (player == null)
            {
                _logger.LogWarning("MQTT command for unknown player id {Id}", cmd.PlayerClientId);
                return;
            }

            switch (cmd.Kind)
            {
                case MqttCommandKind.PlayerOffset when cmd.IntValue is { } ms:
                    _players.SetDelayOffset(player.Name, ms);
                    break;
                case MqttCommandKind.PlayerRestart:
                    await _players.RestartPlayerAsync(player.Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle MQTT command");
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_shuttingDown)
            return;

        LastError = e.Exception?.Message ?? e.ReasonString;
        _logger.LogWarning("MQTT disconnected: {Reason}. Reconnecting...", LastError);

        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (_client != null && _options != null && !_client.IsConnected)
                await ConnectAndAnnounceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT reconnect attempt failed; will retry on next disconnect");
        }
    }

    private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        if (_client is null)
            return;
        await _publishLock.WaitAsync(ct);
        try
        {
            // The broker already retains the last value, so re-sending identical bytes is
            // pure noise for subscribers (#256).
            if (!_retained.ShouldPublish(topic, payload, retain))
                return;

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.PublishAsync(message, ct);
            _retained.Record(topic, payload, retain);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    /// <summary>
    /// Republishes container/bridge state after all startup phases finish, so the
    /// Home Assistant "Ready" sensor reflects completion even on an idle system.
    /// Safe to call when MQTT is disabled or not connected (no-op). Never throws.
    /// </summary>
    public async Task PublishStartupCompleteAsync(CancellationToken ct)
    {
        try
        {
            if (!IsConnected)
                return;
            await PublishDiscoveryAsync(ct);
            await PublishAllStateAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish MQTT state after startup completion");
        }
    }

    /// <summary>Publishes an offline availability message, disconnects from the broker, and disposes resources.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        _shuttingDown = true;
        _players.PlayersChanged -= OnPlayersChanged;
        _triggers.TriggersChanged -= OnTriggersChanged;
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
        if (_client is { IsConnected: true })
        {
            try
            {
                await PublishAvailabilityAsync("offline", ct);
                await _client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during MQTT shutdown");
            }
        }
        _client?.Dispose();
    }
}
