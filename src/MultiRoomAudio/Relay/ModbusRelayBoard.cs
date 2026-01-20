using System.IO.Ports;
using System.Text;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Modbus ASCII relay board implementation for CH340/CH341-based boards.
/// Supports Sainsmart 16-channel and similar boards using Modbus ASCII protocol over serial.
/// These boards use USB-to-serial chips (CH340/CH341) with VID 0x1A86, PID 0x7523.
/// </summary>
/// <remarks>
/// Protocol: Modbus ASCII
/// - Commands are ASCII-encoded hex strings starting with ':' and ending with CR+LF
/// - Device address: 0xFE (254)
/// - Function code 0x05: Write single coil (relay control)
/// - Relay addresses: 0x00-0x0F for channels 1-16
/// - ON value: 0xFF00, OFF value: 0x0000
/// - Example ON command for channel 1: :FE050000FF00FE\r\n
/// - Example OFF command for channel 1: :FE0500000000FD\r\n
/// - Board echoes commands back as acknowledgment (no separate response)
/// </remarks>
public sealed class ModbusRelayBoard : IRelayBoard
{
    // CH340/CH341 USB IDs (for device detection)
    public const int VendorId = 0x1A86;  // WCH (Jiangsu Qinheng)
    public const int ProductId = 0x7523; // CH340

    // Modbus protocol constants
    private const byte DeviceAddress = 0xFE;  // 254
    private const byte FunctionWriteCoil = 0x05;
    private const byte FunctionWriteMultipleCoils = 0x0F;
    private const int BaudRate = 9600;
    private const int CommandDelayMs = 100;  // Delay between commands (CH340 limitation)

    private readonly ILogger<ModbusRelayBoard>? _logger;
    private readonly object _lock = new();
    private SerialPort? _serialPort;
    private string? _portName;
    private string? _serialNumber;
    private int _channelCount;
    private ushort _currentState;
    private bool _disposed;

    public ModbusRelayBoard(ILogger<ModbusRelayBoard>? logger = null, int channelCount = 16)
    {
        _logger = logger;
        _channelCount = Math.Clamp(channelCount, 1, 16);
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
        if (_disposed) return false;

        // Find the first available CH340 serial port
        var ports = GetAvailableSerialPorts();
        if (ports.Count == 0)
        {
            _logger?.LogWarning("No serial ports found for Modbus relay board");
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

        _logger?.LogWarning("Failed to open any serial port for Modbus relay board");
        return false;
    }

    /// <inheritdoc />
    public bool OpenBySerial(string serialNumber)
    {
        if (_disposed) return false;

        // For Modbus boards, the "serial" is actually the port name or a port identifier
        // Format can be: /dev/ttyUSB0, COM3, or MODBUS:/dev/ttyUSB0
        var portName = serialNumber;
        if (portName.StartsWith("MODBUS:", StringComparison.OrdinalIgnoreCase))
        {
            portName = portName.Substring(7);
        }

        if (TryOpenPort(portName))
        {
            return true;
        }

        _logger?.LogWarning("Failed to open Modbus relay board at '{Port}'", portName);
        return false;
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
                _serialNumber = $"MODBUS:{portName}";
                _currentState = 0;

                // Clear any pending data
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();

                _logger?.LogInformation(
                    "Modbus relay board connected: Port={Port}, Channels={Channels}",
                    portName, _channelCount);

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

                _logger?.LogInformation("Modbus relay board closed: {Port}", _portName);
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
                // Build Modbus ASCII command for single coil write
                // Format: :FE05XXXXYYYYCC\r\n
                // XXXX = coil address (0-based, so channel 1 = 0x0000)
                // YYYY = value (FF00 = ON, 0000 = OFF)
                // CC = LRC checksum

                byte addressHi = 0x00;
                byte addressLo = (byte)(channel - 1);  // 0-based address
                byte valueHi = on ? (byte)0xFF : (byte)0x00;
                byte valueLo = 0x00;

                var command = BuildModbusCommand(DeviceAddress, FunctionWriteCoil,
                    addressHi, addressLo, valueHi, valueLo);

                _serialPort.Write(command);

                // Wait for echo/acknowledgment
                Thread.Sleep(CommandDelayMs);

                // Read and discard echo (board echoes command back)
                if (_serialPort.BytesToRead > 0)
                {
                    _serialPort.DiscardInBuffer();
                }

                // Update local state
                var oldState = _currentState;
                if (on)
                    _currentState |= (ushort)(1 << (channel - 1));
                else
                    _currentState &= (ushort)~(1 << (channel - 1));

                _logger?.LogDebug("Modbus SetRelay({Channel}, {On}): state 0x{Old:X4}->0x{New:X4}",
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

            // Use local state tracking (board doesn't reliably report state)
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

            try
            {
                // Use function 0x0F (Write Multiple Coils) to turn all off at once
                // Format: :FE0F0000001002XXXXCC\r\n
                // 0000 = start address
                // 0010 = count (16 coils)
                // 02 = byte count (2 bytes for 16 coils)
                // XXXX = coil values (0000 = all off)

                var command = BuildAllRelaysCommand(false);
                _serialPort.Write(command);

                Thread.Sleep(CommandDelayMs);

                if (_serialPort.BytesToRead > 0)
                {
                    _serialPort.DiscardInBuffer();
                }

                _currentState = 0;
                _logger?.LogDebug("Modbus AllOff: all {Channels} relays turned OFF", _channelCount);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to turn all relays off");

                // Fall back to individual commands
                _logger?.LogDebug("Falling back to individual relay commands");
                bool success = true;
                for (int i = 1; i <= _channelCount; i++)
                {
                    if (!SetRelay(i, false))
                        success = false;
                }
                return success;
            }
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

            try
            {
                var command = BuildAllRelaysCommand(true);
                _serialPort.Write(command);

                Thread.Sleep(CommandDelayMs);

                if (_serialPort.BytesToRead > 0)
                {
                    _serialPort.DiscardInBuffer();
                }

                _currentState = (ushort)((1 << _channelCount) - 1);
                _logger?.LogDebug("Modbus AllOn: all {Channels} relays turned ON", _channelCount);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to turn all relays on");

                // Fall back to individual commands
                bool success = true;
                for (int i = 1; i <= _channelCount; i++)
                {
                    if (!SetRelay(i, true))
                        success = false;
                }
                return success;
            }
        }
    }

    /// <summary>
    /// Build a Modbus ASCII command string.
    /// </summary>
    private static string BuildModbusCommand(byte address, byte function, params byte[] data)
    {
        // Calculate LRC checksum
        int sum = address + function;
        foreach (var b in data)
        {
            sum += b;
        }
        byte lrc = (byte)((-sum) & 0xFF);

        // Build ASCII command
        var sb = new StringBuilder();
        sb.Append(':');
        sb.Append(address.ToString("X2"));
        sb.Append(function.ToString("X2"));
        foreach (var b in data)
        {
            sb.Append(b.ToString("X2"));
        }
        sb.Append(lrc.ToString("X2"));
        sb.Append("\r\n");

        return sb.ToString();
    }

    /// <summary>
    /// Build command to set all relays on or off using Write Multiple Coils (0x0F).
    /// </summary>
    private string BuildAllRelaysCommand(bool on)
    {
        // Function 0x0F: Write Multiple Coils
        // Address: 0xFE
        // Start address: 0x0000
        // Quantity: 0x0010 (16 coils)
        // Byte count: 0x02 (2 bytes for 16 coils)
        // Coil values: 0xFFFF (all on) or 0x0000 (all off)

        byte startHi = 0x00;
        byte startLo = 0x00;
        byte countHi = 0x00;
        byte countLo = (byte)_channelCount;
        byte byteCount = (byte)((_channelCount + 7) / 8);

        // Calculate coil values based on channel count
        var coilValues = new byte[byteCount];
        if (on)
        {
            for (int i = 0; i < byteCount; i++)
            {
                int bitsInThisByte = Math.Min(8, _channelCount - (i * 8));
                coilValues[i] = (byte)((1 << bitsInThisByte) - 1);
            }
        }

        // Build data array
        var data = new byte[4 + 1 + byteCount];
        data[0] = startHi;
        data[1] = startLo;
        data[2] = countHi;
        data[3] = countLo;
        data[4] = byteCount;
        Array.Copy(coilValues, 0, data, 5, byteCount);

        return BuildModbusCommand(DeviceAddress, FunctionWriteMultipleCoils, data);
    }

    /// <summary>
    /// Get list of available serial ports that might be CH340 Modbus relay boards.
    /// </summary>
    public static List<string> GetAvailableSerialPorts()
    {
        var ports = new List<string>();

        try
        {
            // On Linux, look for ttyUSB* devices (CH340 creates these)
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // Check /dev for ttyUSB devices
                if (Directory.Exists("/dev"))
                {
                    var usbPorts = Directory.GetFiles("/dev", "ttyUSB*");
                    ports.AddRange(usbPorts);

                    // Also check for cu.usbserial-* on macOS
                    if (OperatingSystem.IsMacOS())
                    {
                        var macPorts = Directory.GetFiles("/dev", "cu.usbserial-*");
                        ports.AddRange(macPorts);
                    }
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                // On Windows, use SerialPort.GetPortNames()
                ports.AddRange(SerialPort.GetPortNames());
            }
        }
        catch (Exception)
        {
            // Ignore enumeration errors
        }

        return ports;
    }

    /// <summary>
    /// Enumerate available Modbus relay board devices.
    /// Returns serial ports that could be CH340-based relay boards.
    /// </summary>
    public static List<ModbusRelayDeviceInfo> EnumerateDevices(ILogger? logger = null)
    {
        var result = new List<ModbusRelayDeviceInfo>();

        try
        {
            var ports = GetAvailableSerialPorts();

            foreach (var port in ports)
            {
                // We can't definitively identify a CH340 relay board without probing it,
                // but we can list available serial ports
                result.Add(new ModbusRelayDeviceInfo(
                    PortName: port,
                    Description: GetPortDescription(port),
                    IsAvailable: true
                ));

                logger?.LogDebug("Found potential Modbus relay port: {Port}", port);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Error enumerating serial ports");
        }

        return result;
    }

    /// <summary>
    /// Get a human-readable description for a serial port.
    /// </summary>
    private static string GetPortDescription(string portName)
    {
        if (portName.Contains("ttyUSB"))
            return $"USB Serial Port ({Path.GetFileName(portName)})";
        if (portName.Contains("usbserial"))
            return $"USB Serial Port ({Path.GetFileName(portName)})";
        if (portName.StartsWith("COM"))
            return $"Serial Port ({portName})";

        return $"Serial Port ({Path.GetFileName(portName)})";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}

/// <summary>
/// Information about a detected Modbus relay board device (serial port).
/// </summary>
public record ModbusRelayDeviceInfo(
    string PortName,
    string Description,
    bool IsAvailable
)
{
    /// <summary>
    /// Get the board identifier for this device.
    /// </summary>
    public string GetBoardId() => $"MODBUS:{PortName}";
}
