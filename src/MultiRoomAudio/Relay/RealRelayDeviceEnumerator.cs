using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Real implementation of relay device enumeration.
/// Discovers actual FTDI, HID, Modbus, and LCUS relay boards connected to the system.
/// </summary>
public class RealRelayDeviceEnumerator : IRelayDeviceEnumerator
{
    private readonly ILogger<RealRelayDeviceEnumerator> _logger;

    public RealRelayDeviceEnumerator(ILogger<RealRelayDeviceEnumerator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsHardwareAvailable =>
        FtdiRelayBoard.IsLibraryAvailable() ||
        HidRelayBoard.EnumerateDevices(_logger).Count > 0 ||
        Ch340RelayProbe.EnumerateDevices(_logger).Count > 0;

    /// <inheritdoc />
    public List<FtdiDeviceInfo> GetFtdiDevices()
    {
        if (!FtdiRelayBoard.IsLibraryAvailable())
        {
            _logger.LogDebug("FTDI library not available");
            return new List<FtdiDeviceInfo>();
        }

        try
        {
            var devices = FtdiRelayBoard.EnumerateDevices();
            _logger.LogDebug("Found {Count} FTDI devices", devices.Count);
            return devices;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error enumerating FTDI devices");
            return new List<FtdiDeviceInfo>();
        }
    }

    /// <inheritdoc />
    public List<RelayDeviceInfo> GetAllDevices()
    {
        var result = new List<RelayDeviceInfo>();

        // Enumerate FTDI devices
        if (FtdiRelayBoard.IsLibraryAvailable())
        {
            try
            {
                foreach (var ftdi in FtdiRelayBoard.EnumerateDevices())
                {
                    result.Add(RelayDeviceInfo.FromFtdi(ftdi));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error enumerating FTDI devices");
            }
        }

        // Enumerate USB HID relay devices
        // Always use path-based IDs for HID boards to ensure consistency.
        // Serial numbers on these boards are often duplicated across units from the same
        // manufacturer, which would cause ID collisions when adding additional boards.
        try
        {
            foreach (var hid in HidRelayBoard.EnumerateDevices(_logger))
            {
                // Use stable hash for consistent IDs across process restarts
                var boardId = $"HID:{HidRelayDeviceInfo.StableHash(hid.DevicePath)}";

                result.Add(new RelayDeviceInfo(
                    BoardId: boardId,
                    BoardType: RelayBoardType.UsbHid,
                    SerialNumber: hid.SerialNumber,
                    Description: hid.ProductName ?? "USB HID Relay Board",
                    ChannelCount: hid.ChannelCount,
                    IsInUse: false, // We don't track this here - TriggerService manages it
                    UsbPath: hid.DevicePath,
                    IsPathBased: true,
                    ChannelCountDetected: hid.ChannelCountDetected,
                    IsAccessible: hid.IsAccessible,
                    AccessError: hid.AccessError
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error enumerating HID relay devices");
        }

        // Enumerate CH340 serial relay devices (Modbus and LCUS protocols)
        try
        {
            foreach (var ch340 in Ch340RelayProbe.EnumerateDevices(_logger))
            {
                var boardType = ch340.Protocol switch
                {
                    Ch340Protocol.Modbus => RelayBoardType.Modbus,
                    Ch340Protocol.Lcus => RelayBoardType.Lcus,
                    _ => RelayBoardType.Unknown
                };

                result.Add(new RelayDeviceInfo(
                    BoardId: ch340.GetBoardId(),
                    BoardType: boardType,
                    SerialNumber: ch340.UsbPortPath, // Store USB port path if available
                    Description: ch340.Description,
                    ChannelCount: ch340.ChannelCount,
                    IsInUse: false,
                    UsbPath: ch340.PortName, // Current serial port name
                    IsPathBased: ch340.IsPathBased, // True if we have USB port path
                    ChannelCountDetected: ch340.Protocol == Ch340Protocol.Lcus // LCUS auto-detects channel count
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error enumerating CH340 relay devices");
        }

        _logger.LogDebug("Found {Count} total relay devices", result.Count);
        return result;
    }
}
