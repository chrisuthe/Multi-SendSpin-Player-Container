using System.Security.Cryptography;
using System.Text;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Information about a detected Modbus relay board device (serial port).
/// </summary>
public record ModbusRelayDeviceInfo(
    string PortName,
    string Description,
    bool IsAvailable,
    string? UsbPortPath = null
)
{
    /// <summary>
    /// Get the board identifier for this device.
    /// Uses USB port path hash if available (stable), otherwise falls back to port name (unstable).
    /// </summary>
    public string GetBoardId()
    {
        if (!string.IsNullOrEmpty(UsbPortPath))
        {
            // Use stable hash of USB port path - consistent across reboots
            return $"MODBUS:{StableHash(UsbPortPath)}";
        }
        // Fallback to port name (unstable - can change between reboots)
        return $"MODBUS:{PortName}";
    }

    /// <summary>
    /// Whether this device is identified by USB port path (stable) or port name (unstable).
    /// </summary>
    public bool IsPathBased => !string.IsNullOrEmpty(UsbPortPath);

    /// <summary>
    /// Compute a stable 8-character hash from a string.
    /// Uses MD5 for deterministic results across process restarts and platforms.
    /// </summary>
    internal static string StableHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return $"{hash[0]:X2}{hash[1]:X2}{hash[2]:X2}{hash[3]:X2}";
    }
}
