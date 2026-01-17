using System.Collections.Concurrent;
using System.Timers;
using MultiRoomAudio.Models;
using MultiRoomAudio.Relay;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Timer = System.Timers.Timer;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages 12V trigger relays for amplifier zones.
/// Maps custom sinks to relay channels and controls power based on playback state.
/// </summary>
/// <remarks>
/// The trigger feature:
/// - Activates a relay when any player starts streaming to a mapped custom sink
/// - Keeps the relay on while playback is active
/// - Turns off the relay after a configurable delay when playback stops
/// - Supports graceful shutdown (all relays off)
/// - Persists configuration to YAML
/// </remarks>
public class TriggerService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<TriggerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CustomSinksService _sinksService;
    private readonly EnvironmentService _environment;
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly object _configLock = new();
    private readonly object _stateLock = new();

    private FtdiRelayBoard? _relayBoard;
    private TriggerFeatureConfiguration _config = new();
    private TriggerFeatureState _state = TriggerFeatureState.Disabled;
    private string? _errorMessage;
    private bool _disposed;

    // Track active triggers and their off timers
    private readonly ConcurrentDictionary<int, TriggerChannelState> _channelStates = new();

    /// <summary>
    /// Internal state for a trigger channel.
    /// </summary>
    private class TriggerChannelState
    {
        public bool IsActive { get; set; }
        public DateTime? LastActivated { get; set; }
        public Timer? OffDelayTimer { get; set; }
        public int ActivePlayerCount { get; set; }
    }

    public TriggerService(
        ILogger<TriggerService> logger,
        ILoggerFactory loggerFactory,
        CustomSinksService sinksService,
        EnvironmentService environment)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _sinksService = sinksService;
        _environment = environment;
        _configPath = Path.Combine(environment.ConfigPath, "triggers.yaml");

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
            .Build();

        // Initialize channel states (will be adjusted when config is loaded)
        InitializeChannelStates(8);
    }

    private void InitializeChannelStates(int channelCount)
    {
        // Ensure we have states for all channels up to the max
        for (int i = 1; i <= 16; i++)
        {
            if (!_channelStates.ContainsKey(i))
            {
                _channelStates[i] = new TriggerChannelState();
            }
        }
    }

    #region IHostedService

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TriggerService starting...");

        // Load configuration
        _config = LoadConfiguration();

        if (_config.Enabled)
        {
            // Try to connect to the relay board
            ConnectToBoard();
        }
        else
        {
            _state = TriggerFeatureState.Disabled;
            _logger.LogInformation("Trigger feature is disabled");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TriggerService stopping - turning off all relays...");

        // Cancel all pending off timers
        foreach (var kvp in _channelStates)
        {
            kvp.Value.OffDelayTimer?.Stop();
            kvp.Value.OffDelayTimer?.Dispose();
            kvp.Value.OffDelayTimer = null;
        }

        // Turn off all relays on graceful shutdown
        _relayBoard?.AllOff();
        _relayBoard?.Dispose();
        _relayBoard = null;

        _logger.LogInformation("TriggerService stopped");
        return Task.CompletedTask;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Get the current status of the trigger feature.
    /// </summary>
    public TriggerFeatureResponse GetStatus()
    {
        var triggers = new List<TriggerResponse>();
        var channelCount = _config.ChannelCount;

        for (int channel = 1; channel <= channelCount; channel++)
        {
            var config = _config.Triggers.FirstOrDefault(t => t.Channel == channel)
                ?? new TriggerConfiguration { Channel = channel };

            var channelState = _channelStates.GetValueOrDefault(channel) ?? new TriggerChannelState();

            // Get display name for the sink
            string? sinkDisplayName = null;
            if (!string.IsNullOrEmpty(config.CustomSinkName))
            {
                var sink = _sinksService.GetSink(config.CustomSinkName);
                sinkDisplayName = sink?.Description ?? sink?.Name ?? config.CustomSinkName;
            }

            var relayState = _relayBoard?.GetRelay(channel) ?? RelayState.Unknown;

            triggers.Add(new TriggerResponse(
                Channel: channel,
                CustomSinkName: config.CustomSinkName,
                CustomSinkDisplayName: sinkDisplayName,
                OffDelaySeconds: config.OffDelaySeconds,
                ZoneName: config.ZoneName,
                RelayState: relayState,
                IsActive: channelState.IsActive,
                LastActivated: channelState.LastActivated,
                ScheduledOffTime: channelState.OffDelayTimer?.Enabled == true
                    ? channelState.LastActivated?.AddSeconds(config.OffDelaySeconds)
                    : null
            ));
        }

        return new TriggerFeatureResponse(
            Enabled: _config.Enabled,
            State: _state,
            ChannelCount: channelCount,
            DevicePath: _config.DevicePath,
            FtdiSerialNumber: _config.FtdiSerialNumber,
            ErrorMessage: _errorMessage,
            Triggers: triggers,
            CurrentRelayStates: _relayBoard?.CurrentState ?? 0
        );
    }

    /// <summary>
    /// Enable or disable the trigger feature.
    /// </summary>
    public bool SetEnabled(bool enabled, string? ftdiSerialNumber = null, int? channelCount = null)
    {
        lock (_stateLock)
        {
            _config.Enabled = enabled;
            _config.FtdiSerialNumber = ftdiSerialNumber;

            // Update channel count if provided
            if (channelCount.HasValue && ValidChannelCounts.IsValid(channelCount.Value))
            {
                var oldCount = _config.ChannelCount;
                _config.ChannelCount = channelCount.Value;

                // If reducing channels, turn off and unassign triggers beyond new count
                if (channelCount.Value < oldCount)
                {
                    for (int ch = channelCount.Value + 1; ch <= oldCount; ch++)
                    {
                        CancelOffTimer(ch);
                        _relayBoard?.SetRelay(ch, false);
                        var state = _channelStates.GetValueOrDefault(ch);
                        if (state != null)
                        {
                            state.IsActive = false;
                            state.ActivePlayerCount = 0;
                        }
                        // Remove trigger config for channels beyond the new count
                        _config.Triggers.RemoveAll(t => t.Channel > channelCount.Value);
                    }
                }

                _logger.LogInformation("Channel count changed from {Old} to {New}", oldCount, channelCount.Value);
            }

            if (enabled)
            {
                var result = ConnectToBoard();
                SaveConfiguration();
                return result;
            }
            else
            {
                // Disable: turn off all relays and disconnect
                _relayBoard?.AllOff();
                _relayBoard?.Dispose();
                _relayBoard = null;
                _state = TriggerFeatureState.Disabled;
                _errorMessage = null;
                SaveConfiguration();
                _logger.LogInformation("Trigger feature disabled");
                return true;
            }
        }
    }

    /// <summary>
    /// Update just the channel count without changing enabled state.
    /// </summary>
    public bool SetChannelCount(int channelCount)
    {
        if (!ValidChannelCounts.IsValid(channelCount))
        {
            _logger.LogWarning("Invalid channel count: {Count}", channelCount);
            return false;
        }

        lock (_stateLock)
        {
            var oldCount = _config.ChannelCount;
            if (oldCount == channelCount)
                return true;

            _config.ChannelCount = channelCount;

            // If reducing channels, turn off and unassign triggers beyond new count
            if (channelCount < oldCount)
            {
                for (int ch = channelCount + 1; ch <= oldCount; ch++)
                {
                    CancelOffTimer(ch);
                    _relayBoard?.SetRelay(ch, false);
                    var state = _channelStates.GetValueOrDefault(ch);
                    if (state != null)
                    {
                        state.IsActive = false;
                        state.ActivePlayerCount = 0;
                    }
                }
                // Remove trigger configs beyond the new count
                _config.Triggers.RemoveAll(t => t.Channel > channelCount);
            }

            SaveConfiguration();
            _logger.LogInformation("Channel count changed from {Old} to {New}", oldCount, channelCount);
            return true;
        }
    }

    /// <summary>
    /// Configure a trigger channel.
    /// </summary>
    public bool ConfigureTrigger(int channel, string? customSinkName, int offDelaySeconds, string? zoneName)
    {
        if (channel < 1 || channel > _config.ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be between 1 and {_config.ChannelCount}");

        lock (_configLock)
        {
            // Find or create the trigger config
            var trigger = _config.Triggers.FirstOrDefault(t => t.Channel == channel);
            if (trigger == null)
            {
                trigger = new TriggerConfiguration { Channel = channel };
                _config.Triggers.Add(trigger);
            }

            // Validate custom sink exists (if specified)
            if (!string.IsNullOrEmpty(customSinkName))
            {
                var sink = _sinksService.GetSink(customSinkName);
                if (sink == null)
                {
                    _logger.LogWarning("Custom sink '{SinkName}' not found for trigger {Channel}",
                        customSinkName, channel);
                    // Allow configuration anyway - sink might be created later
                }
            }

            trigger.CustomSinkName = customSinkName;
            trigger.OffDelaySeconds = offDelaySeconds;
            trigger.ZoneName = zoneName;

            // If unassigning, turn off the relay and cancel timer
            if (string.IsNullOrEmpty(customSinkName))
            {
                CancelOffTimer(channel);
                _relayBoard?.SetRelay(channel, false);
                var state = _channelStates.GetValueOrDefault(channel);
                if (state != null)
                {
                    state.IsActive = false;
                    state.ActivePlayerCount = 0;
                }
            }

            SaveConfiguration();
            _logger.LogInformation("Trigger {Channel} configured: sink={Sink}, delay={Delay}s, zone={Zone}",
                channel, customSinkName ?? "(none)", offDelaySeconds, zoneName ?? "(none)");

            return true;
        }
    }

    /// <summary>
    /// Manually control a relay (for testing).
    /// </summary>
    public bool ManualControl(int channel, bool on)
    {
        if (channel < 1 || channel > _config.ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be between 1 and {_config.ChannelCount}");

        if (_relayBoard == null || !_relayBoard.IsConnected)
        {
            _logger.LogWarning("Cannot control relay - board not connected");
            return false;
        }

        // Cancel any pending off timer
        if (on)
        {
            CancelOffTimer(channel);
        }

        var result = _relayBoard.SetRelay(channel, on);
        if (result)
        {
            _logger.LogInformation("Manual control: relay {Channel} set to {State}", channel, on ? "ON" : "OFF");
        }

        return result;
    }

    /// <summary>
    /// Get list of available FTDI devices.
    /// </summary>
    public List<FtdiDeviceInfo> GetAvailableDevices()
    {
        if (!FtdiRelayBoard.IsLibraryAvailable())
        {
            return new List<FtdiDeviceInfo>();
        }

        return FtdiRelayBoard.EnumerateDevices();
    }

    /// <summary>
    /// Called when a player starts playing to a device.
    /// </summary>
    public void OnPlayerStarted(string playerName, string? deviceId)
    {
        if (!_config.Enabled || _relayBoard == null || string.IsNullOrEmpty(deviceId))
            return;

        // Find the trigger channel for this device/sink
        var trigger = _config.Triggers.FirstOrDefault(t =>
            !string.IsNullOrEmpty(t.CustomSinkName) &&
            string.Equals(t.CustomSinkName, deviceId, StringComparison.OrdinalIgnoreCase));

        if (trigger == null)
            return;

        ActivateTrigger(trigger.Channel, playerName);
    }

    /// <summary>
    /// Called when a player stops playing.
    /// </summary>
    public void OnPlayerStopped(string playerName, string? deviceId)
    {
        if (!_config.Enabled || _relayBoard == null || string.IsNullOrEmpty(deviceId))
            return;

        var trigger = _config.Triggers.FirstOrDefault(t =>
            !string.IsNullOrEmpty(t.CustomSinkName) &&
            string.Equals(t.CustomSinkName, deviceId, StringComparison.OrdinalIgnoreCase));

        if (trigger == null)
            return;

        DeactivateTrigger(trigger.Channel, trigger.OffDelaySeconds, playerName);
    }

    /// <summary>
    /// Called when a custom sink is deleted - unassign any triggers using it.
    /// </summary>
    public void OnSinkDeleted(string sinkName)
    {
        lock (_configLock)
        {
            var affectedTriggers = _config.Triggers
                .Where(t => string.Equals(t.CustomSinkName, sinkName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var trigger in affectedTriggers)
            {
                _logger.LogInformation("Unassigning trigger {Channel} - sink '{SinkName}' was deleted",
                    trigger.Channel, sinkName);

                trigger.CustomSinkName = null;

                // Turn off relay and cancel timer
                CancelOffTimer(trigger.Channel);
                _relayBoard?.SetRelay(trigger.Channel, false);
                var state = _channelStates.GetValueOrDefault(trigger.Channel);
                if (state != null)
                {
                    state.IsActive = false;
                    state.ActivePlayerCount = 0;
                }
            }

            if (affectedTriggers.Count > 0)
            {
                SaveConfiguration();
            }
        }
    }

    #endregion

    #region Private Methods

    private bool ConnectToBoard()
    {
        lock (_stateLock)
        {
            _relayBoard?.Dispose();
            _relayBoard = null;
            _errorMessage = null;

            if (!FtdiRelayBoard.IsLibraryAvailable())
            {
                _state = TriggerFeatureState.Error;
                _errorMessage = "FTDI library (libftd2xx) not available. Install the FTDI D2XX driver.";
                _logger.LogWarning("FTDI library not available");
                return false;
            }

            _relayBoard = new FtdiRelayBoard(_loggerFactory.CreateLogger<FtdiRelayBoard>());

            bool connected;
            if (!string.IsNullOrEmpty(_config.FtdiSerialNumber))
            {
                connected = _relayBoard.OpenBySerial(_config.FtdiSerialNumber);
            }
            else
            {
                connected = _relayBoard.Open();
            }

            if (connected)
            {
                _state = TriggerFeatureState.Connected;
                _errorMessage = null;
                _logger.LogInformation("Connected to FTDI relay board");
                return true;
            }
            else
            {
                _state = TriggerFeatureState.Disconnected;
                _errorMessage = "Failed to connect to FTDI relay board. Check USB connection and that ftdi_sio is unloaded.";
                _relayBoard.Dispose();
                _relayBoard = null;
                _logger.LogWarning("Failed to connect to FTDI relay board");
                return false;
            }
        }
    }

    private void ActivateTrigger(int channel, string playerName)
    {
        var state = _channelStates.GetValueOrDefault(channel);
        if (state == null)
            return;

        lock (_stateLock)
        {
            // Cancel any pending off timer
            CancelOffTimer(channel);

            // Increment active player count
            state.ActivePlayerCount++;
            state.LastActivated = DateTime.UtcNow;

            // Turn on relay if not already on
            if (!state.IsActive)
            {
                state.IsActive = true;
                _relayBoard?.SetRelay(channel, true);
                _logger.LogInformation("Trigger {Channel} activated by player '{Player}'", channel, playerName);
            }
            else
            {
                _logger.LogDebug("Trigger {Channel} already active, player '{Player}' joined (count: {Count})",
                    channel, playerName, state.ActivePlayerCount);
            }
        }
    }

    private void DeactivateTrigger(int channel, int offDelaySeconds, string playerName)
    {
        var state = _channelStates.GetValueOrDefault(channel);
        if (state == null)
            return;

        lock (_stateLock)
        {
            // Decrement active player count
            state.ActivePlayerCount = Math.Max(0, state.ActivePlayerCount - 1);

            _logger.LogDebug("Player '{Player}' stopped on trigger {Channel} (remaining: {Count})",
                playerName, channel, state.ActivePlayerCount);

            // Only start off delay if no players are active
            if (state.ActivePlayerCount == 0 && state.IsActive)
            {
                if (offDelaySeconds <= 0)
                {
                    // Immediate off
                    state.IsActive = false;
                    _relayBoard?.SetRelay(channel, false);
                    _logger.LogInformation("Trigger {Channel} deactivated immediately", channel);
                }
                else
                {
                    // Start off delay timer
                    StartOffTimer(channel, offDelaySeconds);
                    _logger.LogInformation("Trigger {Channel} will deactivate in {Delay} seconds",
                        channel, offDelaySeconds);
                }
            }
        }
    }

    private void StartOffTimer(int channel, int delaySeconds)
    {
        var state = _channelStates.GetValueOrDefault(channel);
        if (state == null)
            return;

        CancelOffTimer(channel);

        var timer = new Timer(delaySeconds * 1000);
        timer.AutoReset = false;
        timer.Elapsed += (_, _) => OnOffTimerElapsed(channel);
        state.OffDelayTimer = timer;
        timer.Start();
    }

    private void CancelOffTimer(int channel)
    {
        var state = _channelStates.GetValueOrDefault(channel);
        if (state?.OffDelayTimer != null)
        {
            state.OffDelayTimer.Stop();
            state.OffDelayTimer.Dispose();
            state.OffDelayTimer = null;
        }
    }

    private void OnOffTimerElapsed(int channel)
    {
        var state = _channelStates.GetValueOrDefault(channel);
        if (state == null)
            return;

        lock (_stateLock)
        {
            // Check if still should turn off (no new players started)
            if (state.ActivePlayerCount == 0 && state.IsActive)
            {
                state.IsActive = false;
                state.OffDelayTimer = null;
                _relayBoard?.SetRelay(channel, false);
                _logger.LogInformation("Trigger {Channel} deactivated after delay", channel);
            }
        }
    }

    private TriggerFeatureConfiguration LoadConfiguration()
    {
        lock (_configLock)
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogDebug("Trigger config not found at {Path}, using defaults", _configPath);
                return new TriggerFeatureConfiguration();
            }

            try
            {
                var yaml = File.ReadAllText(_configPath);
                if (string.IsNullOrWhiteSpace(yaml))
                    return new TriggerFeatureConfiguration();

                var config = _deserializer.Deserialize<TriggerFeatureConfiguration>(yaml);
                _logger.LogInformation("Loaded trigger configuration: enabled={Enabled}, {Count} triggers configured",
                    config?.Enabled ?? false, config?.Triggers?.Count ?? 0);

                return config ?? new TriggerFeatureConfiguration();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load trigger configuration from {Path}", _configPath);
                return new TriggerFeatureConfiguration();
            }
        }
    }

    private void SaveConfiguration()
    {
        lock (_configLock)
        {
            try
            {
                // Remove unconfigured triggers before saving
                _config.Triggers = _config.Triggers
                    .Where(t => !string.IsNullOrEmpty(t.CustomSinkName) ||
                                !string.IsNullOrEmpty(t.ZoneName) ||
                                t.OffDelaySeconds != 60)
                    .ToList();

                var yaml = _serializer.Serialize(_config);
                File.WriteAllText(_configPath, yaml);
                _logger.LogDebug("Saved trigger configuration");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save trigger configuration to {Path}", _configPath);
            }
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}
