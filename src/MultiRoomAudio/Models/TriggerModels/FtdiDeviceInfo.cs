using System.Security.Cryptography;
using System.Text;

namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Information about a detected FTDI device.
/// </summary>
public record FtdiDeviceInfo(
    int Index,
    string? SerialNumber,
    string? Description,
    bool IsOpen,
    string? UsbPath = null
)
{
    /// <summary>
    /// Get the stable identifier for this device.
    /// Always uses USB path hash for consistency with HID/Modbus boards.
    /// </summary>
    /// <remarks>
    /// ID formats:
    /// - Path-based: "FTDI:8HEXCHARS" (MD5 hash of USB path, stable across reboots)
    /// - Index-based: "FTDI-00" (fallback, unstable - only if libusb unavailable)
    /// </remarks>
    public string GetBoardId()
    {
        // Always use path-based hash for consistency with HID/Modbus boards
        // This avoids collisions when multiple boards have the same serial number
        if (!string.IsNullOrWhiteSpace(UsbPath))
            return $"FTDI:{StableHash(UsbPath)}";

        // Last resort - index-based, unstable (shouldn't happen with libusb)
        return $"FTDI-{Index:D2}";
    }

    /// <summary>
    /// Whether this device is identified by USB port path.
    /// Always true for FTDI boards (matches HID/Modbus behavior).
    /// </summary>
    public bool IsPortBased => true;

    /// <summary>
    /// Compute a stable hash of the USB path for device identification.
    /// Uses MD5 to produce a short, consistent identifier.
    /// </summary>
    /// <param name="input">The USB path string (e.g., "1-3.2")</param>
    /// <returns>8-character hex string (first 4 bytes of MD5)</returns>
    public static string StableHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        // Take first 4 bytes for an 8-char hex string
        return $"{hash[0]:X2}{hash[1]:X2}{hash[2]:X2}{hash[3]:X2}";
    }
};
