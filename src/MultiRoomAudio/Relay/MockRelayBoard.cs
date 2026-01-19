using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Mock relay board for testing without hardware.
/// Simulates an 8-channel relay board.
/// </summary>
public sealed class MockRelayBoard : IRelayBoard
{
    private readonly ILogger<MockRelayBoard>? _logger;
    private readonly ushort[] _relayStates = new ushort[16]; // Support up to 16 channels
    private bool _isConnected;
    private bool _disposed;

    public MockRelayBoard(ILogger<MockRelayBoard>? logger = null)
    {
        _logger = logger;
        _logger?.LogInformation("Mock relay board initialized");
    }

    public bool IsConnected => _isConnected;

    public int CurrentState
    {
        get
        {
            int state = 0;
            for (int i = 0; i < 16; i++)
            {
                if (_relayStates[i] == 1)
                    state |= (1 << i);
            }
            return state;
        }
    }

    public bool Open()
    {
        if (_disposed)
            return false;

        _isConnected = true;
        _logger?.LogInformation("Mock relay board opened (simulated)");
        return true;
    }

    public bool OpenBySerial(string serialNumber)
    {
        if (_disposed)
            return false;

        _isConnected = true;
        _logger?.LogInformation("Mock relay board opened by serial '{Serial}' (simulated)", serialNumber);
        return true;
    }

    public void Close()
    {
        _isConnected = false;
        _logger?.LogInformation("Mock relay board closed");
    }

    public bool SetRelay(int channel, bool on)
    {
        if (!_isConnected || channel < 1 || channel > 16)
            return false;

        _relayStates[channel - 1] = (ushort)(on ? 1 : 0);
        _logger?.LogInformation("Mock relay {Channel} set to {State}", channel, on ? "ON" : "OFF");
        return true;
    }

    public RelayState GetRelay(int channel)
    {
        if (!_isConnected || channel < 1 || channel > 16)
            return RelayState.Unknown;

        return _relayStates[channel - 1] == 1 ? RelayState.On : RelayState.Off;
    }

    public bool AllOff()
    {
        if (!_isConnected)
            return false;

        for (int i = 0; i < _relayStates.Length; i++)
        {
            _relayStates[i] = 0;
        }

        _logger?.LogInformation("Mock relay board: all relays turned OFF");
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _isConnected = false;
        _logger?.LogInformation("Mock relay board disposed");
    }
}
