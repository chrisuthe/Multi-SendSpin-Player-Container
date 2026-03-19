using System.Text;

namespace MultiRoomAudio.Relay.Ch340;

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
