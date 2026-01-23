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
/// Supports multiple relay boards simultaneously.
/// </summary>
public class TriggerService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<TriggerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CustomSinksService _sinksService;
    private readonly IRelayDeviceEnumerator _deviceEnumerator;
    private readonly IRelayBoardFactory _boardFactory;
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly object _configLock = new();
    private readonly object _stateLock = new();

    // Multi-board support: Dictionary of board ID -> relay board instance
    private readonly Dictionary<string, IRelayBoard> _relayBoards = new();
    private readonly Dictionary<string, TriggerFeatureState> _boardStates = new();
    private readonly Dictionary<string, string?> _boardErrors = new();

    private TriggerFeatureConfiguration _config = new();
    private bool _disposed;

    // Track active triggers and their off timers using composite key (boardId, channel)
    private readonly ConcurrentDictionary<(string BoardId, int Channel), TriggerChannelState> _channelStates = new();

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
        EnvironmentService environment,
        IRelayDeviceEnumerator deviceEnumerator,
        IRelayBoardFactory boardFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _sinksService = sinksService;
        _deviceEnumerator = deviceEnumerator;
        _boardFactory = boardFactory;
        _configPath = Path.Combine(environment.ConfigPath, "triggers.yaml");

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
            .Build();
    }

    #region IHostedService

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TriggerService starting...");

        // Load configuration (with migration if needed)
        _config = LoadConfiguration();

        if (_config.Enabled)
        {
            // Connect to all configured boards
            foreach (var boardConfig in _config.Boards)
            {
                ConnectBoard(boardConfig.BoardId);
            }
        }
        else
        {
            _logger.LogInformation("Trigger feature is disabled");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TriggerService stopping...");

        // Cancel all pending off timers
        foreach (var kvp in _channelStates)
        {
            kvp.Value.OffDelayTimer?.Stop();
            kvp.Value.OffDelayTimer?.Dispose();
            kvp.Value.OffDelayTimer = null;
        }

        // Apply shutdown behavior for each board and dispose
        foreach (var boardConfig in _config.Boards)
        {
            if (_relayBoards.TryGetValue(boardConfig.BoardId, out var board))
            {
                ApplyShutdownBehavior(board, boardConfig);
                board.Dispose();
            }
        }
        _relayBoards.Clear();
        _boardStates.Clear();
        _boardErrors.Clear();

        _logger.LogInformation("TriggerService stopped");
        return Task.CompletedTask;
    }

    #endregion

    #region Public API - Feature Status

    /// <summary>
    /// Get the current status of the trigger feature (all boards).
    /// </summary>
    public TriggerFeatureResponse GetStatus()
    {
        var boardResponses = new List<TriggerBoardResponse>();

        foreach (var boardConfig in _config.Boards)
        {
            var boardResponse = GetBoardStatus(boardConfig.BoardId);
            if (boardResponse != null)
            {
                boardResponses.Add(boardResponse);
            }
        }

        var totalChannels = _config.Boards.Sum(b => b.ChannelCount);

        return new TriggerFeatureResponse(
            Enabled: _config.Enabled,
            Boards: boardResponses,
            TotalChannels: totalChannels
        );
    }

    /// <summary>
    /// Get status for a specific board.
    /// </summary>
    public TriggerBoardResponse? GetBoardStatus(string boardId)
    {
        var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
        if (boardConfig == null)
            return null;

        _relayBoards.TryGetValue(boardId, out var relayBoard);
        _boardStates.TryGetValue(boardId, out var state);
        _boardErrors.TryGetValue(boardId, out var errorMessage);

        var isConnected = relayBoard?.IsConnected ?? false;
        var isPortBased = boardId.StartsWith("USB:");

        var triggers = new List<TriggerResponse>();
        for (int channel = 1; channel <= boardConfig.ChannelCount; channel++)
        {
            var config = boardConfig.Triggers.FirstOrDefault(t => t.Channel == channel)
                ?? new TriggerConfiguration { Channel = channel };

            var channelState = _channelStates.GetValueOrDefault((boardId, channel)) ?? new TriggerChannelState();

            // Get display name for the sink
            string? sinkDisplayName = null;
            if (!string.IsNullOrEmpty(config.CustomSinkName))
            {
                var sink = _sinksService.GetSink(config.CustomSinkName);
                sinkDisplayName = sink?.Description ?? sink?.Name ?? config.CustomSinkName;
            }

            var relayState = relayBoard?.GetRelay(channel) ?? RelayState.Unknown;

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

        return new TriggerBoardResponse(
            BoardId: boardId,
            DisplayName: boardConfig.DisplayName,
            BoardType: boardConfig.BoardType,
            IsConnected: isConnected,
            State: state,
            ChannelCount: boardConfig.ChannelCount,
            UsbPath: boardConfig.UsbPath,
            IsPortBased: isPortBased,
            ErrorMessage: errorMessage,
            Triggers: triggers,
            CurrentRelayStates: relayBoard?.CurrentState ?? 0,
            StartupBehavior: boardConfig.StartupBehavior,
            ShutdownBehavior: boardConfig.ShutdownBehavior
        );
    }

    #endregion

    #region Public API - Feature Enable/Disable

    /// <summary>
    /// Enable or disable the trigger feature (affects all boards).
    /// </summary>
    public bool SetEnabled(bool enabled)
    {
        lock (_stateLock)
        {
            _config.Enabled = enabled;

            if (enabled)
            {
                // Connect to all configured boards
                foreach (var boardConfig in _config.Boards)
                {
                    ConnectBoard(boardConfig.BoardId);
                }
                SaveConfiguration();
                return true;
            }
            else
            {
                // Disable: turn off all relays and disconnect all boards
                foreach (var board in _relayBoards.Values)
                {
                    board.AllOff();
                    board.Dispose();
                }
                _relayBoards.Clear();
                _boardStates.Clear();
                _boardErrors.Clear();

                SaveConfiguration();
                _logger.LogInformation("Trigger feature disabled");
                return true;
            }
        }
    }

    #endregion

    #region Public API - Board Management

    /// <summary>
    /// Add a new relay board.
    /// </summary>
    public bool AddBoard(string boardId, string? displayName, int channelCount, RelayBoardType boardType = RelayBoardType.Unknown)
    {
        if (string.IsNullOrWhiteSpace(boardId))
            throw new ArgumentException("Board ID is required", nameof(boardId));

        if (!ValidChannelCounts.IsValid(channelCount))
            channelCount = ValidChannelCounts.Clamp(channelCount);

        // Infer board type from ID if not specified
        if (boardType == RelayBoardType.Unknown)
        {
            if (boardId.StartsWith("HID:", StringComparison.OrdinalIgnoreCase))
                boardType = RelayBoardType.UsbHid;
            else if (boardId.StartsWith("MODBUS:", StringComparison.OrdinalIgnoreCase))
                boardType = RelayBoardType.Modbus;
            else
                boardType = RelayBoardType.Ftdi;
        }

        lock (_configLock)
        {
            // Check if board already exists
            if (_config.Boards.Any(b => b.BoardId == boardId))
            {
                _logger.LogWarning("Board '{BoardId}' already exists", boardId);
                return false;
            }

            var boardConfig = new TriggerBoardConfiguration
            {
                BoardId = boardId,
                DisplayName = displayName,
                BoardType = boardType,
                ChannelCount = channelCount,
                UsbPath = boardId.StartsWith("USB:") ? boardId.Substring(4) : null
            };

            _config.Boards.Add(boardConfig);

            // Initialize channel states for this board
            for (int i = 1; i <= 16; i++)
            {
                _channelStates.TryAdd((boardId, i), new TriggerChannelState());
            }

            // Connect if feature is enabled
            if (_config.Enabled)
            {
                ConnectBoard(boardId);
            }

            SaveConfiguration();
            _logger.LogInformation("Added {BoardType} board '{BoardId}' ({DisplayName}) with {Channels} channels",
                boardType, boardId, displayName ?? boardId, channelCount);

            return true;
        }
    }

    /// <summary>
    /// Remove a relay board.
    /// </summary>
    public bool RemoveBoard(string boardId)
    {
        lock (_configLock)
        {
            var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
            if (boardConfig == null)
            {
                _logger.LogWarning("Board '{BoardId}' not found", boardId);
                return false;
            }

            // Disconnect and turn off relays
            if (_relayBoards.TryGetValue(boardId, out var board))
            {
                board.AllOff();
                board.Dispose();
                _relayBoards.Remove(boardId);
            }
            _boardStates.Remove(boardId);
            _boardErrors.Remove(boardId);

            // Cancel all timers for this board
            for (int ch = 1; ch <= 16; ch++)
            {
                CancelOffTimer(boardId, ch);
                _channelStates.TryRemove((boardId, ch), out _);
            }

            _config.Boards.Remove(boardConfig);
            SaveConfiguration();

            _logger.LogInformation("Removed board '{BoardId}'", boardId);
            return true;
        }
    }

    /// <summary>
    /// Update a board's settings.
    /// </summary>
    public bool UpdateBoard(string boardId, string? displayName, int? channelCount, RelayStartupBehavior? startupBehavior = null, RelayStartupBehavior? shutdownBehavior = null)
    {
        lock (_configLock)
        {
            var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
            if (boardConfig == null)
            {
                _logger.LogWarning("Board '{BoardId}' not found", boardId);
                return false;
            }

            if (displayName != null)
            {
                boardConfig.DisplayName = displayName;
            }

            if (channelCount.HasValue)
            {
                var newCount = ValidChannelCounts.IsValid(channelCount.Value)
                    ? channelCount.Value
                    : ValidChannelCounts.Clamp(channelCount.Value);

                var oldCount = boardConfig.ChannelCount;
                boardConfig.ChannelCount = newCount;

                // If reducing channels, turn off relays beyond new count
                if (newCount < oldCount && _relayBoards.TryGetValue(boardId, out var board))
                {
                    for (int ch = newCount + 1; ch <= oldCount; ch++)
                    {
                        CancelOffTimer(boardId, ch);
                        board.SetRelay(ch, false);
                        if (_channelStates.TryGetValue((boardId, ch), out var state))
                        {
                            state.IsActive = false;
                            state.ActivePlayerCount = 0;
                        }
                    }
                    // Remove trigger configs beyond new count
                    boardConfig.Triggers.RemoveAll(t => t.Channel > newCount);
                }
            }

            if (startupBehavior.HasValue)
            {
                boardConfig.StartupBehavior = startupBehavior.Value;
                _logger.LogInformation("Board '{BoardId}' startup behavior set to {Behavior}", boardId, startupBehavior.Value);
            }

            if (shutdownBehavior.HasValue)
            {
                boardConfig.ShutdownBehavior = shutdownBehavior.Value;
                _logger.LogInformation("Board '{BoardId}' shutdown behavior set to {Behavior}", boardId, shutdownBehavior.Value);
            }

            SaveConfiguration();
            return true;
        }
    }

    /// <summary>
    /// Reconnect a specific board.
    /// </summary>
    public bool ReconnectBoard(string boardId)
    {
        var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
        if (boardConfig == null)
        {
            _logger.LogWarning("Board '{BoardId}' not found", boardId);
            return false;
        }

        // Don't apply startup behavior on manual reconnect - preserve current relay state
        return ConnectBoard(boardId, applyStartupBehavior: false);
    }

    #endregion

    #region Public API - Channel Configuration

    /// <summary>
    /// Configure a trigger channel on a specific board.
    /// </summary>
    public bool ConfigureTrigger(string boardId, int channel, string? customSinkName, int offDelaySeconds, string? zoneName)
    {
        var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
        if (boardConfig == null)
            throw new ArgumentException($"Board '{boardId}' not found", nameof(boardId));

        if (channel < 1 || channel > boardConfig.ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be between 1 and {boardConfig.ChannelCount}");

        lock (_configLock)
        {
            // Find or create the trigger config
            var trigger = boardConfig.Triggers.FirstOrDefault(t => t.Channel == channel);
            if (trigger == null)
            {
                trigger = new TriggerConfiguration { Channel = channel };
                boardConfig.Triggers.Add(trigger);
            }

            // Validate custom sink exists (if specified)
            if (!string.IsNullOrEmpty(customSinkName))
            {
                var sink = _sinksService.GetSink(customSinkName);
                if (sink == null)
                {
                    _logger.LogWarning("Custom sink '{SinkName}' not found for trigger {BoardId}/{Channel}",
                        customSinkName, boardId, channel);
                }
            }

            trigger.CustomSinkName = customSinkName;
            trigger.OffDelaySeconds = offDelaySeconds;
            trigger.ZoneName = zoneName;

            // If unassigning, turn off the relay and cancel timer
            if (string.IsNullOrEmpty(customSinkName))
            {
                CancelOffTimer(boardId, channel);
                if (_relayBoards.TryGetValue(boardId, out var board))
                {
                    board.SetRelay(channel, false);
                }
                if (_channelStates.TryGetValue((boardId, channel), out var state))
                {
                    state.IsActive = false;
                    state.ActivePlayerCount = 0;
                }
            }

            SaveConfiguration();
            _logger.LogInformation("Trigger {BoardId}/{Channel} configured: sink={Sink}, delay={Delay}s, zone={Zone}",
                boardId, channel, customSinkName ?? "(none)", offDelaySeconds, zoneName ?? "(none)");

            return true;
        }
    }

    /// <summary>
    /// Manually control a relay (for testing).
    /// </summary>
    public bool ManualControl(string boardId, int channel, bool on)
    {
        var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
        if (boardConfig == null)
            throw new ArgumentException($"Board '{boardId}' not found", nameof(boardId));

        if (channel < 1 || channel > boardConfig.ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be between 1 and {boardConfig.ChannelCount}");

        if (!_relayBoards.TryGetValue(boardId, out var board) || !board.IsConnected)
        {
            _logger.LogWarning("Cannot control relay - board '{BoardId}' not connected", boardId);
            return false;
        }

        // Cancel any pending off timer
        if (on)
        {
            CancelOffTimer(boardId, channel);
        }

        var result = board.SetRelay(channel, on);
        if (result)
        {
            _logger.LogInformation("Manual control: {BoardId}/{Channel} set to {State}", boardId, channel, on ? "ON" : "OFF");
        }

        return result;
    }

    #endregion

    #region Public API - Device Discovery

    /// <summary>
    /// Get list of available FTDI devices (legacy API for backward compatibility).
    /// </summary>
    public List<FtdiDeviceInfo> GetAvailableDevices()
    {
        return _deviceEnumerator.GetFtdiDevices();
    }

    /// <summary>
    /// Get unified list of all available relay devices (FTDI and HID).
    /// </summary>
    public List<RelayDeviceInfo> GetAllAvailableDevices()
    {
        // Get devices from the enumerator
        var devices = _deviceEnumerator.GetAllDevices();

        // Update IsInUse flag based on which boards are currently connected
        return devices.Select(d => d with { IsInUse = _relayBoards.ContainsKey(d.BoardId) }).ToList();
    }

    #endregion

    #region Public API - Player Events

    /// <summary>
    /// Called when a player starts playing to a device.
    /// </summary>
    public void OnPlayerStarted(string playerName, string? deviceId)
    {
        if (!_config.Enabled || string.IsNullOrEmpty(deviceId))
            return;

        // Find the trigger for this device across all boards
        foreach (var boardConfig in _config.Boards)
        {
            var trigger = boardConfig.Triggers.FirstOrDefault(t =>
                !string.IsNullOrEmpty(t.CustomSinkName) &&
                string.Equals(t.CustomSinkName, deviceId, StringComparison.OrdinalIgnoreCase));

            if (trigger != null && _relayBoards.ContainsKey(boardConfig.BoardId))
            {
                ActivateTrigger(boardConfig.BoardId, trigger.Channel, playerName);
                return;
            }
        }
    }

    /// <summary>
    /// Called when a player stops playing.
    /// </summary>
    public void OnPlayerStopped(string playerName, string? deviceId)
    {
        if (!_config.Enabled || string.IsNullOrEmpty(deviceId))
            return;

        foreach (var boardConfig in _config.Boards)
        {
            var trigger = boardConfig.Triggers.FirstOrDefault(t =>
                !string.IsNullOrEmpty(t.CustomSinkName) &&
                string.Equals(t.CustomSinkName, deviceId, StringComparison.OrdinalIgnoreCase));

            if (trigger != null && _relayBoards.ContainsKey(boardConfig.BoardId))
            {
                DeactivateTrigger(boardConfig.BoardId, trigger.Channel, trigger.OffDelaySeconds, playerName);
                return;
            }
        }
    }

    /// <summary>
    /// Called when a custom sink is deleted - unassign any triggers using it.
    /// </summary>
    public void OnSinkDeleted(string sinkName)
    {
        lock (_configLock)
        {
            var affected = false;

            foreach (var boardConfig in _config.Boards)
            {
                var affectedTriggers = boardConfig.Triggers
                    .Where(t => string.Equals(t.CustomSinkName, sinkName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var trigger in affectedTriggers)
                {
                    _logger.LogInformation("Unassigning trigger {BoardId}/{Channel} - sink '{SinkName}' was deleted",
                        boardConfig.BoardId, trigger.Channel, sinkName);

                    trigger.CustomSinkName = null;

                    // Turn off relay and cancel timer
                    CancelOffTimer(boardConfig.BoardId, trigger.Channel);
                    if (_relayBoards.TryGetValue(boardConfig.BoardId, out var board))
                    {
                        board.SetRelay(trigger.Channel, false);
                    }
                    if (_channelStates.TryGetValue((boardConfig.BoardId, trigger.Channel), out var state))
                    {
                        state.IsActive = false;
                        state.ActivePlayerCount = 0;
                    }
                    affected = true;
                }
            }

            if (affected)
            {
                SaveConfiguration();
            }
        }
    }

    #endregion

    #region Private Methods - Board Connection

    private bool ConnectBoard(string boardId, bool applyStartupBehavior = true)
    {
        lock (_stateLock)
        {
            // Save existing state before disposing (for reconnect scenarios)
            int? previousState = null;
            if (_relayBoards.TryGetValue(boardId, out var existingBoard))
            {
                previousState = existingBoard.CurrentState;
                existingBoard.Dispose();
                _relayBoards.Remove(boardId);
            }

            var boardConfig = _config.Boards.FirstOrDefault(b => b.BoardId == boardId);
            if (boardConfig == null)
            {
                _logger.LogWarning("Board config for '{BoardId}' not found", boardId);
                return false;
            }

            // Initialize channel states for this board
            for (int i = 1; i <= 16; i++)
            {
                _channelStates.TryAdd((boardId, i), new TriggerChannelState());
            }

            // Determine board type from ID if not set
            var boardType = boardConfig.BoardType;
            if (boardType == RelayBoardType.Unknown)
            {
                if (boardId.StartsWith("HID:", StringComparison.OrdinalIgnoreCase))
                    boardType = RelayBoardType.UsbHid;
                else if (boardId.StartsWith("MODBUS:", StringComparison.OrdinalIgnoreCase))
                    boardType = RelayBoardType.Modbus;
                else
                    boardType = RelayBoardType.Ftdi;
            }

            // Check if the factory can create this board type
            if (!_boardFactory.CanCreate(boardId, boardType))
            {
                _boardStates[boardId] = TriggerFeatureState.Error;
                _boardErrors[boardId] = $"Cannot create {boardType} board - required library not available.";
                _logger.LogWarning("Cannot create {BoardType} board '{BoardId}' - library not available", boardType, boardId);
                return false;
            }

            // Create the board using the factory
            var board = _boardFactory.CreateBoard(boardId, boardType);

            // Connect to the board
            bool connected;

            string? connectionError = null;

            if (boardId.StartsWith("HID:", StringComparison.OrdinalIgnoreCase))
            {
                // HID boards always use path-based identification (hash of device path)
                var pathHash = boardId.Substring(4);
                if (board is HidRelayBoard hidBoard)
                {
                    connected = hidBoard.OpenByPathHash(pathHash, out connectionError);
                }
                else
                {
                    _logger.LogWarning("Board '{BoardId}' is HID type but factory didn't create HidRelayBoard", boardId);
                    connected = board.Open();
                }
            }
            else if (boardId.StartsWith("USB:", StringComparison.OrdinalIgnoreCase))
            {
                // USB port-based board - open first available
                connected = board.Open();
            }
            else if (boardId.StartsWith("MODBUS:", StringComparison.OrdinalIgnoreCase))
            {
                var modbusId = boardId.Substring(7);
                // Check if it's a USB path hash (8 hex chars) or a port name (/dev/ttyUSB0)
                var isHashId = modbusId.Length == 8 && modbusId.All(c => char.IsAsciiHexDigit(c));

                if (isHashId && board is ModbusRelayBoard modbusBoard)
                {
                    // Hash-based identification - find by USB port path
                    connected = modbusBoard.OpenByUsbPathHash(modbusId);
                }
                else
                {
                    // Legacy port-name based identification
                    connected = board.OpenBySerial(boardId);
                }
            }
            else if (boardId.StartsWith("FTDI:", StringComparison.OrdinalIgnoreCase))
            {
                // FTDI path-based identification (hash of USB bus/port path)
                var pathHash = boardId.Substring(5);
                if (board is FtdiRelayBoard ftdiBoard)
                {
                    connected = ftdiBoard.OpenByPathHash(pathHash);
                }
                else
                {
                    _logger.LogWarning("Board '{BoardId}' is FTDI type but factory didn't create FtdiRelayBoard", boardId);
                    connected = board.Open();
                }
            }
            else
            {
                // FTDI with serial number (raw serial as ID)
                var serial = GetSerialFromBoardId(boardId);
                connected = string.IsNullOrEmpty(serial) ? board.Open() : board.OpenBySerial(serial);
            }

            if (connected)
            {
                _relayBoards[boardId] = board;
                _boardStates[boardId] = TriggerFeatureState.Connected;
                _boardErrors[boardId] = null;

                // Update board type if it was Unknown
                if (boardConfig.BoardType == RelayBoardType.Unknown)
                {
                    boardConfig.BoardType = boardType;
                    SaveConfiguration();
                }

                // Update channel count if it was auto-detected and different from config
                if (board.ChannelCount != boardConfig.ChannelCount && board.ChannelCount > 0)
                {
                    _logger.LogInformation("Board '{BoardId}' reports {Detected} channels, config had {Config}",
                        boardId, board.ChannelCount, boardConfig.ChannelCount);
                    boardConfig.ChannelCount = board.ChannelCount;
                    SaveConfiguration();
                }

                // Apply startup behavior or restore previous state
                if (applyStartupBehavior)
                {
                    ApplyStartupBehavior(board, boardConfig);
                }
                else if (previousState.HasValue)
                {
                    RestorePreviousState(board, boardConfig, previousState.Value);
                }
                else
                {
                    _logger.LogInformation("Board '{BoardId}': Skipping startup behavior (manual reconnect, no previous state)", boardId);
                }

                _logger.LogInformation("Connected to {BoardType} relay board '{BoardId}' (Serial: {Serial}, {Channels} channels, startup: {Startup})",
                    boardType, boardId, board.SerialNumber ?? "(none)", board.ChannelCount,
                    applyStartupBehavior ? boardConfig.StartupBehavior.ToString() : "skipped");
                return true;
            }
            else
            {
                _boardStates[boardId] = TriggerFeatureState.Disconnected;
                // Use detailed error from connection attempt if available
                _boardErrors[boardId] = connectionError ?? $"Failed to connect to {boardType} relay board. Check USB connection.";
                board.Dispose();
                _logger.LogWarning("Failed to connect to {BoardType} relay board '{BoardId}': {Error}",
                    boardType, boardId, connectionError ?? "Unknown error");
                return false;
            }
        }
    }

    /// <summary>
    /// Extract the serial number from a board ID.
    /// Handles HID:serial, USB:path, and MODBUS:port formats.
    /// </summary>
    private static string? GetSerialFromBoardId(string boardId)
    {
        if (boardId.StartsWith("HID:", StringComparison.OrdinalIgnoreCase))
            return boardId.Substring(4);
        if (boardId.StartsWith("USB:", StringComparison.OrdinalIgnoreCase))
            return null; // Port-based, no serial
        if (boardId.StartsWith("MODBUS:", StringComparison.OrdinalIgnoreCase))
            return boardId; // Modbus uses the full ID including port path
        return boardId; // Assume the ID is the serial itself (FTDI)
    }

    /// <summary>
    /// Restore previous relay state on manual reconnect.
    /// </summary>
    private void RestorePreviousState(IRelayBoard board, TriggerBoardConfiguration config, int previousState)
    {
        for (int i = 1; i <= config.ChannelCount; i++)
        {
            bool wasOn = (previousState & (1 << (i - 1))) != 0;
            board.SetRelay(i, wasOn);
        }
        _logger.LogInformation("Board '{BoardId}': Restored previous relay state 0x{State:X2} (manual reconnect)", config.BoardId, previousState);
    }

    /// <summary>
    /// Apply the configured startup behavior to a newly connected relay board.
    /// </summary>
    private void ApplyStartupBehavior(IRelayBoard board, TriggerBoardConfiguration config)
    {
        // Log hardware state before applying startup behavior (FTDI boards only)
        LogHardwareState(board, config.BoardId, config.ChannelCount, "before startup behavior");

        switch (config.StartupBehavior)
        {
            case RelayStartupBehavior.AllOff:
                board.AllOff();
                _logger.LogInformation("Board '{BoardId}': All relays set to OFF (startup behavior)", config.BoardId);
                break;

            case RelayStartupBehavior.AllOn:
                for (int i = 1; i <= config.ChannelCount; i++)
                {
                    board.SetRelay(i, true);
                }
                _logger.LogInformation("Board '{BoardId}': All relays set to ON (startup behavior)", config.BoardId);
                break;

            case RelayStartupBehavior.NoChange:
                _logger.LogInformation("Board '{BoardId}': Relay state preserved (startup behavior)", config.BoardId);
                break;
        }

        // Log hardware state after applying startup behavior
        LogHardwareState(board, config.BoardId, config.ChannelCount, "after startup behavior");
    }

    /// <summary>
    /// Apply the configured shutdown behavior when the service is stopping.
    /// </summary>
    private void ApplyShutdownBehavior(IRelayBoard board, TriggerBoardConfiguration config)
    {
        // Log hardware state before applying shutdown behavior
        LogHardwareState(board, config.BoardId, config.ChannelCount, "before shutdown behavior");

        switch (config.ShutdownBehavior)
        {
            case RelayStartupBehavior.AllOff:
                board.AllOff();
                _logger.LogInformation("Board '{BoardId}': All relays set to OFF (shutdown behavior)", config.BoardId);
                break;

            case RelayStartupBehavior.AllOn:
                for (int i = 1; i <= config.ChannelCount; i++)
                {
                    board.SetRelay(i, true);
                }
                _logger.LogInformation("Board '{BoardId}': All relays set to ON (shutdown behavior)", config.BoardId);
                break;

            case RelayStartupBehavior.NoChange:
                _logger.LogInformation("Board '{BoardId}': Relay state preserved (shutdown behavior)", config.BoardId);
                break;
        }

        // Log hardware state after applying shutdown behavior
        LogHardwareState(board, config.BoardId, config.ChannelCount, "after shutdown behavior");
    }

    /// <summary>
    /// Log the actual hardware state of a relay board (if supported).
    /// </summary>
    private void LogHardwareState(IRelayBoard board, string boardId, int channelCount, string context)
    {
        byte? hardwareState = board.ReadHardwareState();
        if (hardwareState.HasValue)
        {
            string activeRelays = FormatActiveRelays(hardwareState.Value, channelCount);
            _logger.LogInformation(
                "Board '{BoardId}': Hardware state {Context}: 0x{State:X2} ({ActiveRelays})",
                boardId, context, hardwareState.Value, activeRelays);
        }
        // If null, hardware read not supported or failed - already logged by the board
    }

    /// <summary>
    /// Format active relay channels as a human-readable string.
    /// </summary>
    private static string FormatActiveRelays(byte state, int channelCount)
    {
        if (state == 0)
            return "none active";

        var active = new List<string>();
        for (int i = 0; i < channelCount && i < 8; i++)
        {
            if ((state & (1 << i)) != 0)
                active.Add($"CH{i + 1}");
        }
        return string.Join(", ", active);
    }

    #endregion

    #region Private Methods - Trigger Activation

    private void ActivateTrigger(string boardId, int channel, string playerName)
    {
        if (!_channelStates.TryGetValue((boardId, channel), out var state))
            return;

        lock (_stateLock)
        {
            // Cancel any pending off timer
            CancelOffTimer(boardId, channel);

            // Increment active player count
            state.ActivePlayerCount++;
            state.LastActivated = DateTime.UtcNow;

            // Turn on relay if not already on
            if (!state.IsActive)
            {
                state.IsActive = true;
                if (_relayBoards.TryGetValue(boardId, out var board))
                {
                    board.SetRelay(channel, true);
                }
                _logger.LogInformation("Trigger {BoardId}/{Channel} activated by player '{Player}'", boardId, channel, playerName);
            }
            else
            {
                _logger.LogDebug("Trigger {BoardId}/{Channel} already active, player '{Player}' joined (count: {Count})",
                    boardId, channel, playerName, state.ActivePlayerCount);
            }
        }
    }

    private void DeactivateTrigger(string boardId, int channel, int offDelaySeconds, string playerName)
    {
        if (!_channelStates.TryGetValue((boardId, channel), out var state))
            return;

        lock (_stateLock)
        {
            // Decrement active player count
            state.ActivePlayerCount = Math.Max(0, state.ActivePlayerCount - 1);

            _logger.LogDebug("Player '{Player}' stopped on trigger {BoardId}/{Channel} (remaining: {Count})",
                playerName, boardId, channel, state.ActivePlayerCount);

            // Only start off delay if no players are active
            if (state.ActivePlayerCount == 0 && state.IsActive)
            {
                if (offDelaySeconds <= 0)
                {
                    // Immediate off
                    state.IsActive = false;
                    if (_relayBoards.TryGetValue(boardId, out var board))
                    {
                        board.SetRelay(channel, false);
                    }
                    _logger.LogInformation("Trigger {BoardId}/{Channel} deactivated immediately", boardId, channel);
                }
                else
                {
                    // Start off delay timer
                    StartOffTimer(boardId, channel, offDelaySeconds);
                    _logger.LogInformation("Trigger {BoardId}/{Channel} will deactivate in {Delay} seconds",
                        boardId, channel, offDelaySeconds);
                }
            }
        }
    }

    private void StartOffTimer(string boardId, int channel, int delaySeconds)
    {
        if (!_channelStates.TryGetValue((boardId, channel), out var state))
            return;

        CancelOffTimer(boardId, channel);

        var timer = new Timer(delaySeconds * 1000);
        timer.AutoReset = false;
        timer.Elapsed += (_, _) => OnOffTimerElapsed(boardId, channel);
        state.OffDelayTimer = timer;
        timer.Start();
    }

    private void CancelOffTimer(string boardId, int channel)
    {
        if (_channelStates.TryGetValue((boardId, channel), out var state) && state.OffDelayTimer != null)
        {
            state.OffDelayTimer.Stop();
            state.OffDelayTimer.Dispose();
            state.OffDelayTimer = null;
        }
    }

    private void OnOffTimerElapsed(string boardId, int channel)
    {
        if (!_channelStates.TryGetValue((boardId, channel), out var state))
            return;

        lock (_stateLock)
        {
            // Check if still should turn off (no new players started)
            if (state.ActivePlayerCount == 0 && state.IsActive)
            {
                state.IsActive = false;
                state.OffDelayTimer = null;
                if (_relayBoards.TryGetValue(boardId, out var board))
                {
                    board.SetRelay(channel, false);
                }
                _logger.LogInformation("Trigger {BoardId}/{Channel} deactivated after delay", boardId, channel);
            }
        }
    }

    #endregion

    #region Private Methods - Configuration

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

#pragma warning disable CS0618 // Obsolete properties are for migration only
                var config = _deserializer.Deserialize<TriggerFeatureConfiguration>(yaml);
#pragma warning restore CS0618

                if (config == null)
                    return new TriggerFeatureConfiguration();

                // Migrate from legacy single-board format if needed
                if (config.NeedsMigration)
                {
                    _logger.LogInformation("Migrating trigger config from single-board to multi-board format");
                    config.MigrateFromLegacy();
                    SaveConfigurationInternal(config);
                }

                _logger.LogInformation("Loaded trigger configuration: enabled={Enabled}, {BoardCount} boards configured",
                    config.Enabled, config.Boards.Count);

                return config;
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
        SaveConfigurationInternal(_config);
    }

    private void SaveConfigurationInternal(TriggerFeatureConfiguration config)
    {
        lock (_configLock)
        {
            try
            {
                // Clean up unconfigured triggers before saving
                foreach (var board in config.Boards)
                {
                    board.Triggers = board.Triggers
                        .Where(t => !string.IsNullOrEmpty(t.CustomSinkName) ||
                                    !string.IsNullOrEmpty(t.ZoneName) ||
                                    t.OffDelaySeconds != 60)
                        .ToList();
                }

                var yaml = _serializer.Serialize(config);
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
