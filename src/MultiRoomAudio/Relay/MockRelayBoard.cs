using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Mock relay board for testing without hardware.
/// Simulates a relay board with configurable identity (serial number, board type, channel count).
/// Used when MOCK_HARDWARE mode is enabled for testing without real relay hardware.
/// </summary>
public sealed class MockRelayBoard : IRelayBoard
{
    private readonly ILogger<MockRelayBoard>? _logger;
    private readonly string _serialNumber;
    private readonly RelayBoardType _boardType;
    private readonly int _channelCount;
    private readonly ushort[] _relayStates = new ushort[16]; // Support up to 16 channels
    private bool _isConnected;
    private bool _disposed;

    /// <summary>
    /// Create a mock relay board with configurable identity.
    /// </summary>
    /// <param name="logger">Optional logger instance.</param>
    /// <param name="serialNumber">Serial number for this mock board. If null, generates a unique ID.</param>
    /// <param name="boardType">Type of board to simulate.</param>
    /// <param name="channelCount">Number of channels (1-16).</param>
    public MockRelayBoard(
        ILogger<MockRelayBoard>? logger = null,
        string? serialNumber = null,
        RelayBoardType boardType = RelayBoardType.Mock,
        int channelCount = 8)
    {
        _logger = logger;
        _serialNumber = serialNumber ?? $"MOCK{Guid.NewGuid():N}".Substring(0, 8);
        _boardType = boardType;
        _channelCount = Math.Clamp(channelCount, 1, 16);

        _logger?.LogInformation(
            "Mock relay board initialized: Serial={Serial}, Type={BoardType}, Channels={Channels}",
            _serialNumber, _boardType, _channelCount);
    }

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public string? SerialNumber => _serialNumber;

    /// <inheritdoc />
    public int ChannelCount => _channelCount;

    /// <summary>
    /// The simulated board type (for diagnostic purposes).
    /// </summary>
    public RelayBoardType BoardType => _boardType;

    /// <inheritdoc />
    public int CurrentState
    {
        get
        {
            int state = 0;
            for (int i = 0; i < _channelCount; i++)
            {
                if (_relayStates[i] == 1)
                    state |= (1 << i);
            }
            return state;
        }
    }

    /// <inheritdoc />
    public bool Open()
    {
        if (_disposed)
            return false;

        _isConnected = true;
        _logger?.LogInformation("Mock relay board '{Serial}' opened (simulated)", _serialNumber);
        return true;
    }

    /// <inheritdoc />
    public bool OpenBySerial(string serialNumber)
    {
        if (_disposed)
            return false;

        // In mock mode, we accept any serial number that matches our configured serial
        // or we just connect anyway (the factory creates the right board for each ID)
        _isConnected = true;
        _logger?.LogInformation("Mock relay board '{Serial}' opened by serial (simulated)", _serialNumber);
        return true;
    }

    /// <inheritdoc />
    public void Close()
    {
        _isConnected = false;
        _logger?.LogInformation("Mock relay board '{Serial}' closed", _serialNumber);
    }

    /// <inheritdoc />
    public bool SetRelay(int channel, bool on)
    {
        if (!_isConnected || channel < 1 || channel > _channelCount)
            return false;

        _relayStates[channel - 1] = (ushort)(on ? 1 : 0);
        _logger?.LogDebug("Mock relay '{Serial}' channel {Channel} set to {State}",
            _serialNumber, channel, on ? "ON" : "OFF");
        return true;
    }

    /// <inheritdoc />
    public RelayState GetRelay(int channel)
    {
        if (!_isConnected || channel < 1 || channel > _channelCount)
            return RelayState.Unknown;

        return _relayStates[channel - 1] == 1 ? RelayState.On : RelayState.Off;
    }

    /// <inheritdoc />
    public bool AllOff()
    {
        if (!_isConnected)
            return false;

        for (int i = 0; i < _channelCount; i++)
        {
            _relayStates[i] = 0;
        }

        _logger?.LogDebug("Mock relay board '{Serial}': all {Channels} relays turned OFF",
            _serialNumber, _channelCount);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _isConnected = false;
        _logger?.LogDebug("Mock relay board '{Serial}' disposed", _serialNumber);
    }
}
