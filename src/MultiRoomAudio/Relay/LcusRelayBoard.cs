using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// LCUS-type relay board implementation for CH340/CH341-based boards.
/// Supports LCUS 1-8 channel relay boards using a simple binary protocol over serial.
/// These boards use USB-to-serial chips (CH340/CH341) with VID 0x1A86, PID 0x7523.
/// </summary>
/// <remarks>
/// Protocol: LCUS Binary
/// - Baud rate: 9600, 8N1
/// - Status query: Send 0xFF, receive N bytes (one per channel)
/// - Command format: [0xA0][Channel 0x01-0x08][Operation 0x00=OFF/0x01=ON][Checksum]
/// - Checksum: (0xA0 + channel + operation) &amp; 0xFF
/// - Example: Channel 1 ON = A0 01 01 A2, Channel 1 OFF = A0 01 00 A1
/// </remarks>
public sealed class LcusRelayBoard : IRelayBoard
{
    // CH340/CH341 USB IDs (same as Modbus - both use same chip)
    public const int VendorId = 0x1A86;  // WCH (Jiangsu Qinheng)
    public const int ProductId = 0x7523; // CH340

    // LCUS protocol constants
    private const byte CommandPrefix = 0xA0;
    private const byte StatusQuery = 0xFF;
    private const int BaudRate = 9600;
    private const int CommandDelayMs = 50;  // Delay between commands

    private readonly ILogger<LcusRelayBoard>? _logger;
    private readonly object _lock = new();
    private SerialPort? _serialPort;
    private string? _portName;
    private string? _serialNumber;
    private int _channelCount;
    private byte _currentState;
    private bool _disposed;

    public LcusRelayBoard(ILogger<LcusRelayBoard>? logger = null, int channelCount = 8)
    {
        _logger = logger;
        _channelCount = Math.Clamp(channelCount, 1, 8);  // LCUS boards max 8 channels
    }

    /// <inheritdoc />
    public bool IsConnected => _serialPort?.IsOpen ?? false;

    /// <inheritdoc />
    public string? SerialNumber => _serialNumber;

    /// <inheritdoc />
    public int ChannelCount => _channelCount;

    /// <inheritdoc />
    public int CurrentState
    {
        get
        {
            lock (_lock)
            {
                return _currentState;
            }
        }
    }

    /// <summary>
    /// The serial port name (e.g., /dev/ttyUSB0, COM3).
    /// </summary>
    public string? PortName => _portName;

    /// <inheritdoc />
    public bool Open()
    {
        if (_disposed)
            return false;

        // Find the first available CH340 serial port that responds to LCUS protocol
        var ports = ModbusRelayBoard.GetAvailableSerialPorts();
        if (ports.Count == 0)
        {
            _logger?.LogWarning("No serial ports found for LCUS relay board");
            return false;
        }

        // Try each port
        foreach (var port in ports)
        {
            if (TryOpenPort(port))
            {
                return true;
            }
        }

        _logger?.LogWarning("Failed to open any serial port for LCUS relay board");
        return false;
    }

    /// <inheritdoc />
    public bool OpenBySerial(string serialNumber)
    {
        if (_disposed)
            return false;

        // For LCUS boards, the "serial" is actually the port name or a port identifier
        // Format can be: /dev/ttyUSB0, COM3, or LCUS:/dev/ttyUSB0
        var portName = serialNumber;
        if (portName.StartsWith("LCUS:", StringComparison.OrdinalIgnoreCase))
        {
            portName = portName.Substring(5);
        }

        if (TryOpenPort(portName))
        {
            return true;
        }

        _logger?.LogWarning("Failed to open LCUS relay board at '{Port}'", portName);
        return false;
    }

    /// <summary>
    /// Open an LCUS relay board by matching the USB port path hash.
    /// Scans available serial ports to find one with matching USB port path.
    /// </summary>
    /// <param name="pathHash">The hash portion of the board ID (e.g., "A1B2C3D4" from "LCUS:A1B2C3D4")</param>
    /// <returns>True if connection was successful.</returns>
    public bool OpenByUsbPathHash(string pathHash)
    {
        if (_disposed)
            return false;

        try
        {
            var devices = Ch340RelayProbe.EnumerateDevices(_logger);
            foreach (var device in devices)
            {
                if (device.Protocol != Ch340Protocol.Lcus || !device.IsPathBased)
                    continue;

                // Calculate the same hash used when enumerating
                var deviceHash = Ch340RelayDeviceInfo.StableHash(device.UsbPortPath!);
                if (deviceHash.Equals(pathHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogDebug("Found LCUS relay board by USB path hash: {Hash} -> {Port}",
                        pathHash, device.PortName);
                    return TryOpenPort(device.PortName);
                }
            }

            _logger?.LogWarning("LCUS relay board with USB path hash '{Hash}' not found", pathHash);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open LCUS relay board by USB path hash '{Hash}'", pathHash);
            return false;
        }
    }

    private bool TryOpenPort(string portName)
    {
        lock (_lock)
        {
            try
            {
                Close();

                _serialPort = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    Handshake = Handshake.None,
                    DtrEnable = false,
                    RtsEnable = false
                };

                _serialPort.Open();

                if (!_serialPort.IsOpen)
                {
                    _logger?.LogDebug("Failed to open serial port {Port}", portName);
                    return false;
                }

                _portName = portName;
                _serialNumber = $"LCUS:{portName}";

                // Clear any pending data
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();

                // Query status to get current state and detect channel count
                var state = QueryStatus();
                if (state == null)
                {
                    _logger?.LogDebug("Port {Port} does not respond to LCUS status query", portName);
                    Close();
                    return false;
                }

                _currentState = state.Value;

                _logger?.LogInformation(
                    "LCUS relay board connected: Port={Port}, Channels={Channels}, State=0x{State:X2}",
                    portName, _channelCount, _currentState);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error opening serial port {Port}", portName);
                _serialPort?.Dispose();
                _serialPort = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Query the board for current relay status.
    /// Returns the state bitmask if board responds, null if no response or error.
    /// </summary>
    private byte? QueryStatus()
    {
        if (_serialPort == null || !_serialPort.IsOpen)
            return null;

        try
        {
            _serialPort.DiscardInBuffer();
            _serialPort.Write(new byte[] { StatusQuery }, 0, 1);
            Thread.Sleep(100);

            var bytesAvailable = _serialPort.BytesToRead;
            if (bytesAvailable == 0)
                return null;

            // The number of bytes returned indicates the channel count
            var buffer = new byte[bytesAvailable];
            _serialPort.Read(buffer, 0, bytesAvailable);

            // Update channel count based on response
            _channelCount = Math.Clamp(bytesAvailable, 1, 8);

            // Convert channel states to bitmask
            byte state = 0;
            for (int i = 0; i < buffer.Length && i < 8; i++)
            {
                if (buffer[i] != 0)
                    state |= (byte)(1 << i);
            }

            return state;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error querying LCUS status");
            return null;
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        lock (_lock)
        {
            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error closing serial port");
                }
                finally
                {
                    _serialPort.Dispose();
                    _serialPort = null;
                }

                _logger?.LogInformation("LCUS relay board closed: {Port}", _portName);
            }

            _portName = null;
        }
    }

    /// <inheritdoc />
    public bool SetRelay(int channel, bool on)
    {
        if (channel < 1 || channel > _channelCount)
        {
            _logger?.LogWarning("Invalid channel {Channel} (board has {Count} channels)", channel, _channelCount);
            return false;
        }

        lock (_lock)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                _logger?.LogWarning("Cannot set relay - serial port not open");
                return false;
            }

            try
            {
                // Build LCUS command: [0xA0][Channel][Operation][Checksum]
                byte channelByte = (byte)channel;
                byte operationByte = on ? (byte)0x01 : (byte)0x00;
                byte checksum = (byte)((CommandPrefix + channelByte + operationByte) & 0xFF);

                var command = new byte[] { CommandPrefix, channelByte, operationByte, checksum };

                _serialPort.Write(command, 0, command.Length);
                Thread.Sleep(CommandDelayMs);

                // Discard any echo
                if (_serialPort.BytesToRead > 0)
                {
                    _serialPort.DiscardInBuffer();
                }

                // Update local state
                var oldState = _currentState;
                if (on)
                    _currentState |= (byte)(1 << (channel - 1));
                else
                    _currentState &= (byte)~(1 << (channel - 1));

                _logger?.LogDebug("LCUS SetRelay({Channel}, {On}): state 0x{Old:X2}->0x{New:X2}",
                    channel, on ? "ON" : "OFF", oldState, _currentState);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to set relay {Channel} to {State}", channel, on ? "ON" : "OFF");
                return false;
            }
        }
    }

    /// <inheritdoc />
    public RelayState GetRelay(int channel)
    {
        if (channel < 1 || channel > _channelCount)
            return RelayState.Unknown;

        lock (_lock)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                return RelayState.Unknown;

            // Use local state tracking
            var isOn = (_currentState & (1 << (channel - 1))) != 0;
            return isOn ? RelayState.On : RelayState.Off;
        }
    }

    /// <inheritdoc />
    public bool AllOff()
    {
        lock (_lock)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                _logger?.LogWarning("Cannot turn all relays off - serial port not open");
                return false;
            }

            bool success = true;
            for (int i = 1; i <= _channelCount; i++)
            {
                if (!SetRelay(i, false))
                    success = false;
            }

            if (success)
            {
                _currentState = 0;
                _logger?.LogDebug("LCUS AllOff: all {Channels} relays turned OFF", _channelCount);
            }

            return success;
        }
    }

    /// <summary>
    /// Turn all relays on at once.
    /// </summary>
    public bool AllOn()
    {
        lock (_lock)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                _logger?.LogWarning("Cannot turn all relays on - serial port not open");
                return false;
            }

            bool success = true;
            for (int i = 1; i <= _channelCount; i++)
            {
                if (!SetRelay(i, true))
                    success = false;
            }

            if (success)
            {
                _currentState = (byte)((1 << _channelCount) - 1);
                _logger?.LogDebug("LCUS AllOn: all {Channels} relays turned ON", _channelCount);
            }

            return success;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
    }
}
