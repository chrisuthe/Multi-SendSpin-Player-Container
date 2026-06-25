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
    private readonly VersionService _version;
    private readonly EnvironmentService _env;
    private readonly StartupProgressService _startup;
    private readonly ILogger<MqttService> _logger;

    private IMqttClient? _client;
    private MqttTopics? _topics;
    private HaDiscovery? _discovery;
    private string _baseTopic = "multiroom-audio";
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private CancellationTokenSource? _reconnectCts;

    public MqttService(
        MqttConfigService config,
        PlayerManagerService players,
        VersionService version,
        EnvironmentService env,
        StartupProgressService startup,
        ILogger<MqttService> logger)
    {
        _config = config;
        _players = players;
        _version = version;
        _env = env;
        _startup = startup;
        _logger = logger;
    }

    public bool IsConnected => _client?.IsConnected ?? false;
    public string? LastError { get; private set; }

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
            .WithClientId($"multiroom-audio-{Environment.MachineName}")
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
    }

    private MqttClientOptions? _options;

    private async Task ConnectAndAnnounceAsync(CancellationToken ct)
    {
        await _client!.ConnectAsync(_options!, ct);
        LastError = null;
        _logger.LogInformation("MQTT bridge connected to broker");

        await _client.SubscribeAsync(_topics!.PlayerCommandSubscription, MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await PublishAvailabilityAsync("online", ct);
        await PublishDiscoveryAsync(ct);
        await PublishAllStateAsync(ct);
    }

    private async Task PublishAvailabilityAsync(string value, CancellationToken ct)
        => await PublishAsync(_topics!.BridgeAvailabilityTopic, value, retain: true, ct);

    private async Task PublishDiscoveryAsync(CancellationToken ct)
    {
        foreach (var p in _players.GetAllPlayers().Players)
            foreach (var m in _discovery!.ForPlayer(p))
                await PublishAsync(m.Topic, m.Payload, retain: true, ct);

        foreach (var m in _discovery!.ForContainer(_env.EnvironmentName))
            await PublishAsync(m.Topic, m.Payload, retain: true, ct);
    }

    private async Task PublishAllStateAsync(CancellationToken ct)
    {
        foreach (var p in _players.GetAllPlayers().Players)
            await PublishAsync(_topics!.PlayerStateTopic(p.ClientId), MqttStatePayloads.Player(p), retain: true, ct);

        var players = _players.GetAllPlayers().Players;
        await PublishAsync(_topics!.ContainerStateTopic,
            MqttStatePayloads.Container(_startup.IsStartupComplete, _version.Version,
                players.Count, _env.AudioBackend, _env.EnvironmentName),
            retain: true, ct);
    }

    private void OnPlayersChanged()
    {
        // Fire-and-forget; serialize via the publish lock. Never throw into the caller.
        _ = Task.Run(async () =>
        {
            try
            {
                if (!IsConnected) return;
                await PublishDiscoveryAsync(CancellationToken.None);
                await PublishAllStateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish MQTT state on player change");
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
            if (cmd.Kind == MqttCommandKind.Unknown) return;

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
        LastError = e.Exception?.Message ?? e.ReasonString;
        _logger.LogWarning("MQTT disconnected: {Reason}. Reconnecting...", LastError);

        var ct = (_reconnectCts ??= new CancellationTokenSource()).Token;
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
        if (_client is null) return;
        await _publishLock.WaitAsync(ct);
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.PublishAsync(message, ct);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        _players.PlayersChanged -= OnPlayersChanged;
        _reconnectCts?.Cancel();
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
