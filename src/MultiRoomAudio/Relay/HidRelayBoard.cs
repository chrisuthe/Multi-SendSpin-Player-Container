using System.Linq;
using HidSharp;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// USB HID relay board implementation for DCT Tech / ucreatefun style boards.
/// These boards use USB HID protocol with VID 0x16C0, PID 0x05DF.
/// Product names are "USBRelayX" where X is the number of channels (1, 2, 4, 8).
/// </summary>
public sealed class HidRelayBoard : IRelayBoard
{
    // DCT Tech / ucreatefun USB HID Relay board identifiers
    public const int VendorId = 0x16C0;  // 5824
    public const int ProductId = 0x05DF; // 1503

    // HID protocol commands
    private const byte CMD_ON = 0xFF;
    private const byte CMD_OFF = 0xFD;

    private readonly ILogger<HidRelayBoard>? _logger;
    private HidDevice? _device;
    private HidStream? _stream;
    private string? _serialNumber;
    private int _channelCount;
    private int _currentState;
    private bool _isConnected;
    private bool _disposed;

    public HidRelayBoard(ILogger<HidRelayBoard>? logger = null)
    {
        _logger = logger;
    }

    public bool IsConnected => _isConnected && _stream != null;

    public int CurrentState => _currentState;

    /// <summary>
    /// The board's serial/identifier (e.g., "QAAMZ").
    /// </summary>
    public string? SerialNumber => _serialNumber;

    /// <summary>
    /// Number of relay channels on this board (auto-detected from product name).
    /// </summary>
    public int ChannelCount => _channelCount;

    public bool Open()
    {
        if (_disposed) return false;

        try
        {
            var device = FindFirstDevice();
            if (device == null)
            {
                _logger?.LogWarning("No USB HID relay board found");
                return false;
            }

            return OpenDevice(device);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open USB HID relay board");
            return false;
        }
    }

    public bool OpenBySerial(string serialNumber)
    {
        if (_disposed) return false;

        try
        {
            var device = FindDeviceBySerial(serialNumber);
            if (device == null)
            {
                _logger?.LogWarning("USB HID relay board with serial '{Serial}' not found", serialNumber);
                return false;
            }

            return OpenDevice(device);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open USB HID relay board by serial '{Serial}'", serialNumber);
            return false;
        }
    }

    private bool OpenDevice(HidDevice device)
    {
        try
        {
            _device = device;
            _stream = device.Open();

            // Read the feature report to get board serial and current state
            var report = new byte[9];
            report[0] = 0; // Report ID
            _stream.GetFeature(report);

            // Extract serial from bytes 1-5 (ASCII characters, sanitized)
            _serialNumber = SanitizeSerial(report, 1, 5);

            // Current relay state is in byte 7
            _currentState = report[7];

            // Detect channel count (tries product name first, then probes if needed)
            _channelCount = DetectChannelCount(device);

            _isConnected = true;
            _logger?.LogInformation(
                "USB HID relay board connected: Serial={Serial}, Channels={Channels}, State={State:X2}",
                _serialNumber, _channelCount, _currentState);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open HID device stream");
            Close();
            return false;
        }
    }

    public void Close()
    {
        _isConnected = false;

        if (_stream != null)
        {
            try { _stream.Close(); } catch { }
            try { _stream.Dispose(); } catch { }
            _stream = null;
        }

        _device = null;
        _logger?.LogInformation("USB HID relay board closed");
    }

    public bool SetRelay(int channel, bool on)
    {
        if (!IsConnected || _stream == null)
            return false;

        if (channel < 1 || channel > _channelCount)
        {
            _logger?.LogWarning("Invalid channel {Channel} (board has {Count} channels)", channel, _channelCount);
            return false;
        }

        try
        {
            // DCTTECH protocol: [report_id, cmd, relay_num, 0, 0, 0, 0, 0, 0]
            // These boards use feature reports for control, not output reports
            var report = new byte[9];
            report[0] = 0;  // Report ID
            report[1] = on ? CMD_ON : CMD_OFF;
            report[2] = (byte)channel;

            _stream.SetFeature(report);

            // Update local state tracking
            var oldState = _currentState;
            if (on)
                _currentState |= (1 << (channel - 1));
            else
                _currentState &= ~(1 << (channel - 1));

            _logger?.LogDebug("HID SetRelay({Channel}, {On}): local state 0x{Old:X2}->0x{New:X2}",
                channel, on ? "ON" : "OFF", oldState, _currentState);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set relay {Channel} to {State}", channel, on ? "ON" : "OFF");
            return false;
        }
    }

    public RelayState GetRelay(int channel)
    {
        if (!IsConnected || channel < 1 || channel > _channelCount)
            return RelayState.Unknown;

        // NOTE: These HID relay boards do NOT reliably report state via GetFeature.
        // The feature report byte[7] returns 0x00 even when relays are on.
        // We rely on local state tracking updated by SetRelay() instead.
        // RefreshState() is NOT called here to avoid overwriting correct local state.

        var isOn = (_currentState & (1 << (channel - 1))) != 0;
        var result = isOn ? RelayState.On : RelayState.Off;
        _logger?.LogDebug("HID GetRelay({Channel}): localState=0x{State:X2}, mask=0x{Mask:X2}, result={Result}",
            channel, _currentState, 1 << (channel - 1), result);
        return result;
    }

    public bool AllOff()
    {
        if (!IsConnected)
            return false;

        bool success = true;
        for (int i = 1; i <= _channelCount; i++)
        {
            if (!SetRelay(i, false))
                success = false;
        }

        return success;
    }

    /// <summary>
    /// Read state from device feature report.
    /// WARNING: Many HID relay boards return 0x00 in byte[7] regardless of actual state.
    /// This method is only used during initial connection, NOT for ongoing state tracking.
    /// </summary>
    private void RefreshState()
    {
        if (_stream == null) return;

        try
        {
            var report = new byte[9];
            report[0] = 0;
            _stream.GetFeature(report);
            _currentState = report[7];
            _logger?.LogDebug("HID RefreshState: byte[7]=0x{StateByte:X2}", report[7]);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to refresh relay state");
        }
    }

    /// <summary>
    /// Sanitize serial number by removing non-printable characters.
    /// Some HID relay boards have garbage bytes in the serial field.
    /// </summary>
    private static string SanitizeSerial(byte[] data, int offset, int length)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = offset; i < offset + length && i < data.Length; i++)
        {
            char c = (char)data[i];
            // Keep only printable ASCII characters (0x20-0x7E)
            if (c >= 0x20 && c <= 0x7E)
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse the channel count from the product name (e.g., "USBRelay8" -> 8).
    /// Returns null if cannot be determined from the name, allowing caller to use configured value.
    ///
    /// Detection method: Parse from USB product string (same as usbrelay tool).
    /// - DCTTECH boards: "USBRelayX" where X is the channel count
    /// - Some boards: "HIDRelayX" format
    /// - For boards where this fails, user must configure manually.
    /// </summary>
    public static int? ParseChannelCountFromName(string? productName)
    {
        if (string.IsNullOrEmpty(productName))
            return null;

        foreach (var prefix in new[] { "USBRelay", "HIDRelay" })
        {
            if (productName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = productName.Substring(prefix.Length);
                if (int.TryParse(suffix, out int count) && count >= 1 && count <= 16)
                    return count;
            }
        }

        return null; // Cannot determine - user must configure
    }

    /// <summary>
    /// Detect channel count from device, falling back to default if not determinable.
    /// </summary>
    private int DetectChannelCount(HidDevice device)
    {
        var productName = device.GetProductName();
        var detected = ParseChannelCountFromName(productName);

        if (detected.HasValue)
        {
            _logger?.LogDebug("Channel count {Count} detected from product name '{Name}'", detected.Value, productName);
            return detected.Value;
        }

        // Cannot detect - use default and log warning
        _logger?.LogWarning("Could not detect channel count from product name '{Name}', using default of 8. Configure manually if incorrect.", productName);
        return 8;
    }

    /// <summary>
    /// Find the first available USB HID relay board.
    /// </summary>
    private static HidDevice? FindFirstDevice()
    {
        var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId);
        return devices.FirstOrDefault();
    }

    /// <summary>
    /// Find a USB HID relay board by its serial number.
    /// </summary>
    private HidDevice? FindDeviceBySerial(string serialNumber)
    {
        var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId);

        foreach (var device in devices)
        {
            try
            {
                // Open temporarily to read the serial from feature report
                using var stream = device.Open();
                var report = new byte[9];
                report[0] = 0;
                stream.GetFeature(report);

                var serial = SanitizeSerial(report, 1, 5);
                if (serial.Equals(serialNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Could not read serial from HID device");
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerate all connected USB HID relay boards.
    /// </summary>
    public static List<HidRelayDeviceInfo> EnumerateDevices(ILogger? logger = null)
    {
        var result = new List<HidRelayDeviceInfo>();

        try
        {
            var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId);

            foreach (var device in devices)
            {
                try
                {
                    string? serial = null;
                    var productName = device.GetProductName();
                    int? detectedChannels = ParseChannelCountFromName(productName);
                    int channelCount = detectedChannels ?? 8; // Default to 8 if not detectable
                    int currentState = 0;

                    // Try to read serial and state from feature report
                    try
                    {
                        using var stream = device.Open();
                        var report = new byte[9];
                        report[0] = 0;
                        stream.GetFeature(report);
                        serial = SanitizeSerial(report, 1, 5);
                        currentState = report[7];
                    }
                    catch
                    {
                        // Device might be in use by another process
                    }

                    result.Add(new HidRelayDeviceInfo(
                        DevicePath: device.DevicePath,
                        SerialNumber: serial,
                        ProductName: productName,
                        ChannelCount: channelCount,
                        CurrentState: currentState,
                        ChannelCountDetected: detectedChannels.HasValue
                    ));

                    logger?.LogDebug(
                        "Found HID relay: Path={Path}, Serial={Serial}, Product={Product}, Channels={Channels} (detected={Detected})",
                        device.DevicePath, serial, productName, channelCount, detectedChannels.HasValue);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error reading HID relay device info");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error enumerating HID relay devices");
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}

/// <summary>
/// Information about a detected USB HID relay board.
/// </summary>
public record HidRelayDeviceInfo(
    string DevicePath,
    string? SerialNumber,
    string? ProductName,
    int ChannelCount,
    int CurrentState,
    bool ChannelCountDetected = true
)
{
    /// <summary>
    /// Get the preferred board identifier.
    /// Uses serial if available, otherwise device path hash.
    /// </summary>
    public string GetBoardId()
    {
        if (!string.IsNullOrWhiteSpace(SerialNumber))
            return $"HID:{SerialNumber}";

        // Fallback to path-based ID
        return $"HID:{DevicePath.GetHashCode():X8}";
    }

    /// <summary>
    /// Whether this device is identified by path (less stable).
    /// </summary>
    public bool IsPathBased => string.IsNullOrWhiteSpace(SerialNumber);
}
