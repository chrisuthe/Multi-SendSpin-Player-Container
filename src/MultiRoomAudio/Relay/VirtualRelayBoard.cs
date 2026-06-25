using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Software-only relay board. Records channel state in memory and is always ready once opened.
/// Its power state reaches Home Assistant through the normal amp-state publish (driven by
/// TriggerService.TriggersChanged) — there is no MQTT dependency in the relay layer itself.
/// </summary>
public sealed class VirtualRelayBoard : IRelayBoard
{
    private readonly ILogger<VirtualRelayBoard>? _logger;
    private readonly string _serialNumber;
    private readonly int _channelCount;
    private readonly bool[] _states = new bool[16];
    private bool _isConnected;
    private bool _disposed;

    public VirtualRelayBoard(ILogger<VirtualRelayBoard>? logger = null, string? serialNumber = null, int channelCount = 8)
    {
        _logger = logger;
        _serialNumber = serialNumber ?? "VIRTUAL";
        _channelCount = Math.Clamp(channelCount, 1, 16);
    }

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public string? SerialNumber => _serialNumber;

    /// <inheritdoc />
    public int ChannelCount => _channelCount;

    /// <inheritdoc />
    public int CurrentState
    {
        get
        {
            int s = 0;
            for (int i = 0; i < _channelCount; i++)
                if (_states[i]) s |= (1 << i);
            return s;
        }
    }

    /// <inheritdoc />
    public bool Open()
    {
        if (_disposed) return false;
        _isConnected = true;
        return true;
    }

    /// <inheritdoc />
    public bool OpenBySerial(string serialNumber) => Open();

    /// <inheritdoc />
    public void Close() => _isConnected = false;

    /// <inheritdoc />
    public bool SetRelay(int channel, bool on)
    {
        if (!_isConnected || channel < 1 || channel > _channelCount) return false;
        _states[channel - 1] = on;
        _logger?.LogDebug("Virtual relay '{Serial}' channel {Channel} → {State}", _serialNumber, channel, on ? "ON" : "OFF");
        return true;
    }

    /// <inheritdoc />
    public RelayState GetRelay(int channel)
    {
        if (!_isConnected || channel < 1 || channel > _channelCount) return RelayState.Unknown;
        return _states[channel - 1] ? RelayState.On : RelayState.Off;
    }

    /// <inheritdoc />
    public bool AllOff()
    {
        if (!_isConnected) return false;
        Array.Clear(_states);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isConnected = false;
    }
}
