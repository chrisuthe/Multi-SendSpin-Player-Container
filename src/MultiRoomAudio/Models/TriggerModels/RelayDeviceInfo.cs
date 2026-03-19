namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Unified information about a detected relay board device (FTDI or HID).
/// Used for device discovery/enumeration in the UI.
/// </summary>
public record RelayDeviceInfo(
    /// <summary>Board identifier to use when adding this device.</summary>
    string BoardId,
    /// <summary>Type of relay board.</summary>
    RelayBoardType BoardType,
    /// <summary>Serial number if available.</summary>
    string? SerialNumber,
    /// <summary>Product/device description.</summary>
    string? Description,
    /// <summary>Number of channels (auto-detected for HID boards).</summary>
    int ChannelCount,
    /// <summary>Whether the device is currently open/in use.</summary>
    bool IsInUse,
    /// <summary>USB path if available.</summary>
    string? UsbPath,
    /// <summary>Whether this device is identified by path (less stable).</summary>
    bool IsPathBased,
    /// <summary>Whether the channel count was auto-detected (true) or needs manual config (false).</summary>
    bool ChannelCountDetected = false,
    /// <summary>Whether the device is accessible (can be opened). False if permission denied or device mapping missing.</summary>
    bool IsAccessible = true,
    /// <summary>Error message if the device is not accessible (e.g., Docker device mapping hint).</summary>
    string? AccessError = null
)
{
    /// <summary>
    /// Create from an FTDI device info.
    /// </summary>
    public static RelayDeviceInfo FromFtdi(FtdiDeviceInfo ftdi) => new(
        BoardId: ftdi.GetBoardId(),
        BoardType: RelayBoardType.Ftdi,
        SerialNumber: ftdi.SerialNumber,
        Description: ftdi.Description ?? "FTDI Relay Board",
        ChannelCount: 8, // FTDI boards need manual channel count config
        IsInUse: ftdi.IsOpen,
        UsbPath: ftdi.UsbPath,
        IsPathBased: ftdi.IsPortBased,
        ChannelCountDetected: false // FTDI boards always need manual config
    );
};
