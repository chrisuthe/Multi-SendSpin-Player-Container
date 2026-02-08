using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Detected CH340 relay board protocol type.
/// </summary>
public enum Ch340Protocol
{
    /// <summary>Device did not respond to any relay protocol.</summary>
    Unknown,

    /// <summary>Device responds to Modbus ASCII protocol (Sainsmart 16-channel, etc.).</summary>
    Modbus,

    /// <summary>Device responds to LCUS binary protocol (LCUS 1-8 channel boards).</summary>
    Lcus
}

/// <summary>
/// Probe result for CH340 serial devices.
/// </summary>
public enum Ch340ProbeResult
{
    /// <summary>Device is a relay board (Modbus or LCUS protocol detected).</summary>
    RelayBoard,

    /// <summary>Device did not respond to any relay protocol.</summary>
    NoResponse,

    /// <summary>Serial port is busy (in use by another process).</summary>
    PortBusy,

    /// <summary>Error during probe.</summary>
    Error
}

/// <summary>
/// Unified probe for CH340-based relay boards.
/// Tries Modbus ASCII first, then LCUS binary protocol.
/// </summary>
public static class Ch340RelayProbe
{
    private const int BaudRate = 9600;
    private const int ProbeTimeoutMs = 500;

    /// <summary>
    /// Probe a serial port to determine if it's a relay board and which protocol it uses.
    /// Tries Modbus first (more reliable detection), then LCUS.
    /// </summary>
    /// <param name="portName">Serial port to probe (e.g., /dev/ttyUSB0, COM3)</param>
    /// <param name="logger">Optional logger for debug output</param>
    /// <returns>Tuple of (ProbeResult, Protocol, ChannelCount)</returns>
    public static (Ch340ProbeResult Result, Ch340Protocol Protocol, int ChannelCount) ProbeDevice(
        string portName,
        ILogger? logger = null)
    {
        SerialPort? port = null;
        try
        {
            port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = ProbeTimeoutMs,
                WriteTimeout = ProbeTimeoutMs,
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false
            };

            port.Open();
            if (!port.IsOpen)
            {
                logger?.LogDebug("CH340 probe: Failed to open {Port}", portName);
                return (Ch340ProbeResult.Error, Ch340Protocol.Unknown, 0);
            }

            // Clear any pending data
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            // Try Modbus first - send "read coils" command
            var modbusResult = TryModbusProbe(port, logger);
            if (modbusResult.Success)
            {
                logger?.LogDebug("CH340 probe: {Port} responds to Modbus protocol", portName);
                return (Ch340ProbeResult.RelayBoard, Ch340Protocol.Modbus, 16); // Modbus boards are typically 16-channel
            }

            // Small delay between probes
            Thread.Sleep(100);
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            // Try LCUS protocol - send status query
            var lcusResult = TryLcusProbe(port, logger);
            if (lcusResult.Success)
            {
                logger?.LogDebug("CH340 probe: {Port} responds to LCUS protocol with {Channels} channels",
                    portName, lcusResult.ChannelCount);
                return (Ch340ProbeResult.RelayBoard, Ch340Protocol.Lcus, lcusResult.ChannelCount);
            }

            // Neither protocol worked
            logger?.LogDebug("CH340 probe: {Port} does not respond to relay protocols", portName);
            return (Ch340ProbeResult.NoResponse, Ch340Protocol.Unknown, 0);
        }
        catch (UnauthorizedAccessException)
        {
            logger?.LogDebug("CH340 probe: {Port} is busy/inaccessible", portName);
            return (Ch340ProbeResult.PortBusy, Ch340Protocol.Unknown, 0);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "CH340 probe: Error probing {Port}", portName);
            return (Ch340ProbeResult.Error, Ch340Protocol.Unknown, 0);
        }
        finally
        {
            try
            {
                port?.Close();
                port?.Dispose();
            }
            catch { /* ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Try Modbus ASCII "read coils" command.
    /// Modbus boards echo the command back as acknowledgment.
    /// </summary>
    private static (bool Success, int ChannelCount) TryModbusProbe(SerialPort port, ILogger? logger)
    {
        try
        {
            // Modbus ASCII "Read Coils" command
            // Address: 0xFE (254)
            // Function: 0x01 (Read Coils)
            // Start: 0x0000
            // Count: 0x0010 (16 coils)
            // LRC: 0xF1
            const string command = ":FE0100000010F1\r\n";

            port.Write(command);
            Thread.Sleep(200);

            var bytesAvailable = port.BytesToRead;
            if (bytesAvailable == 0)
                return (false, 0);

            // Read response
            var buffer = new byte[bytesAvailable];
            port.Read(buffer, 0, bytesAvailable);
            var response = Encoding.ASCII.GetString(buffer);

            // Modbus boards echo the command or send a response starting with ':'
            if (response.StartsWith(":"))
            {
                logger?.LogDebug("Modbus probe: Got response '{Response}'", response.TrimEnd());
                return (true, 16);
            }

            return (false, 0);
        }
        catch (TimeoutException)
        {
            return (false, 0);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Modbus probe error");
            return (false, 0);
        }
    }

    /// <summary>
    /// Try LCUS binary status query.
    /// LCUS boards respond with N bytes (one per channel) indicating relay state.
    /// </summary>
    private static (bool Success, int ChannelCount) TryLcusProbe(SerialPort port, ILogger? logger)
    {
        try
        {
            // LCUS status query: single byte 0xFF
            port.Write(new byte[] { 0xFF }, 0, 1);
            Thread.Sleep(200);

            var bytesAvailable = port.BytesToRead;
            if (bytesAvailable == 0)
                return (false, 0);

            // LCUS boards return 1-8 bytes (one per channel)
            // Each byte is the channel state (0x00 or 0x01)
            if (bytesAvailable >= 1 && bytesAvailable <= 8)
            {
                var buffer = new byte[bytesAvailable];
                port.Read(buffer, 0, bytesAvailable);

                // Validate response: each byte should be 0x00 or 0x01
                bool valid = true;
                foreach (var b in buffer)
                {
                    if (b != 0x00 && b != 0x01)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                {
                    logger?.LogDebug("LCUS probe: Got {Count} channel states: {States}",
                        bytesAvailable,
                        string.Join(" ", buffer.Select(b => b.ToString("X2"))));
                    return (true, bytesAvailable);
                }
            }

            return (false, 0);
        }
        catch (TimeoutException)
        {
            return (false, 0);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "LCUS probe error");
            return (false, 0);
        }
    }

    /// <summary>
    /// Enumerate all CH340 serial ports and detect which ones are relay boards.
    /// </summary>
    public static List<Ch340RelayDeviceInfo> EnumerateDevices(ILogger? logger = null)
    {
        var result = new List<Ch340RelayDeviceInfo>();

        try
        {
            var ports = ModbusRelayBoard.GetAvailableSerialPorts();
            logger?.LogDebug("CH340 probe: Found {Count} serial ports to probe", ports.Count);

            foreach (var port in ports)
            {
                var (probeResult, protocol, channelCount) = ProbeDevice(port, logger);

                if (probeResult != Ch340ProbeResult.RelayBoard)
                {
                    logger?.LogDebug("CH340 probe: Skipping {Port} - {Result}", port, probeResult);
                    continue;
                }

                // Get USB port path for stable identification
                var usbPortPath = GetUsbPortPath(port, logger);

                result.Add(new Ch340RelayDeviceInfo(
                    PortName: port,
                    Description: GetPortDescription(port, protocol, channelCount),
                    Protocol: protocol,
                    ChannelCount: channelCount,
                    UsbPortPath: usbPortPath
                ));

                logger?.LogDebug("CH340 probe: Added {Port} as {Protocol} relay board with {Channels} channels",
                    port, protocol, channelCount);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Error enumerating CH340 relay devices");
        }

        return result;
    }

    /// <summary>
    /// Get the USB port path for a serial port device (Linux only).
    /// </summary>
    private static string? GetUsbPortPath(string portName, ILogger? logger = null)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            var deviceName = Path.GetFileName(portName);
            var sysPath = $"/sys/class/tty/{deviceName}/device";

            if (!Directory.Exists(sysPath))
                return null;

            var targetPath = Path.GetFullPath(sysPath);

            var match = System.Text.RegularExpressions.Regex.Match(
                targetPath,
                @"/(\d+-[\d.]+)(?::\d+\.\d+)?/");

            if (match.Success)
                return match.Groups[1].Value;

            var ueventPath = Path.Combine(sysPath, "..", "..", "uevent");
            if (File.Exists(ueventPath))
            {
                var uevent = File.ReadAllText(ueventPath);
                var devPathMatch = System.Text.RegularExpressions.Regex.Match(
                    uevent,
                    @"DEVPATH=.*/(\d+-[\d.]+)/");
                if (devPathMatch.Success)
                    return devPathMatch.Groups[1].Value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetPortDescription(string portName, Ch340Protocol protocol, int channelCount)
    {
        var protocolName = protocol switch
        {
            Ch340Protocol.Modbus => "Modbus",
            Ch340Protocol.Lcus => "LCUS",
            _ => "Unknown"
        };

        return $"{protocolName} Relay Board ({channelCount}ch, {Path.GetFileName(portName)})";
    }
}

/// <summary>
/// Information about a detected CH340 relay board device.
/// </summary>
public record Ch340RelayDeviceInfo(
    string PortName,
    string Description,
    Ch340Protocol Protocol,
    int ChannelCount,
    string? UsbPortPath = null
)
{
    /// <summary>
    /// Get the board identifier for this device.
    /// Uses protocol prefix + USB port path hash if available.
    /// </summary>
    public string GetBoardId()
    {
        var prefix = Protocol switch
        {
            Ch340Protocol.Modbus => "MODBUS",
            Ch340Protocol.Lcus => "LCUS",
            _ => "CH340"
        };

        if (!string.IsNullOrEmpty(UsbPortPath))
        {
            return $"{prefix}:{StableHash(UsbPortPath)}";
        }

        return $"{prefix}:{PortName}";
    }

    /// <summary>
    /// Whether this device is identified by USB port path (stable) or port name (unstable).
    /// </summary>
    public bool IsPathBased => !string.IsNullOrEmpty(UsbPortPath);

    /// <summary>
    /// Compute a stable 8-character hash from a string.
    /// </summary>
    internal static string StableHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = System.Security.Cryptography.MD5.HashData(bytes);
        return $"{hash[0]:X2}{hash[1]:X2}{hash[2]:X2}{hash[3]:X2}";
    }
}
