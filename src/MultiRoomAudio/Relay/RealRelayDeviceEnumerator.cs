using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Real implementation of relay device enumeration.
/// Discovers actual FTDI and HID relay boards connected to the system.
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
        FtdiRelayBoard.IsLibraryAvailable() || HidRelayBoard.EnumerateDevices(_logger).Count > 0;

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
        try
        {
            foreach (var hid in HidRelayBoard.EnumerateDevices(_logger))
            {
                result.Add(new RelayDeviceInfo(
                    BoardId: hid.GetBoardId(),
                    BoardType: RelayBoardType.UsbHid,
                    SerialNumber: hid.SerialNumber,
                    Description: hid.ProductName ?? "USB HID Relay Board",
                    ChannelCount: hid.ChannelCount,
                    IsInUse: false, // We don't track this here - TriggerService manages it
                    UsbPath: hid.DevicePath,
                    IsPathBased: hid.IsPathBased,
                    ChannelCountDetected: hid.ChannelCountDetected
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error enumerating HID relay devices");
        }

        _logger.LogDebug("Found {Count} total relay devices", result.Count);
        return result;
    }
}
