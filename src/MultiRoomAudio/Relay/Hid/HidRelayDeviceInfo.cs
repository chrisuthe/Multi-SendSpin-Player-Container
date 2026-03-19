using System.Security.Cryptography;
using System.Text;

namespace MultiRoomAudio.Relay.Hid;

/// <summary>
/// Information about a detected USB HID relay board.
/// </summary>
/// <param name="DevicePath">Full sysfs path to the device.</param>
/// <param name="SerialNumber">Serial number read from the device (null if couldn't be read).</param>
/// <param name="ProductName">USB product name (e.g., "USBRelay8").</param>
/// <param name="ChannelCount">Number of relay channels (detected or default).</param>
/// <param name="CurrentState">Relay state bitmask (0 if couldn't be read).</param>
/// <param name="ChannelCountDetected">True if channel count was detected from product name.</param>
/// <param name="IsAccessible">True if the device could be opened during enumeration.</param>
/// <param name="AccessError">Error message if the device couldn't be opened (null if accessible).</param>
public record HidRelayDeviceInfo(
    string DevicePath,
    string? SerialNumber,
    string? ProductName,
    int ChannelCount,
    int CurrentState,
    bool ChannelCountDetected = true,
    bool IsAccessible = true,
    string? AccessError = null
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

        // Fallback to path-based ID using stable hash
        return $"HID:{StableHash(DevicePath)}";
    }

    /// <summary>
    /// Whether this device is identified by path (less stable).
    /// </summary>
    public bool IsPathBased => string.IsNullOrWhiteSpace(SerialNumber);

    /// <summary>
    /// Extract the hidraw device name (e.g., "hidraw1") from a sysfs device path.
    /// Returns null if the path doesn't contain a hidraw reference.
    /// </summary>
    /// <example>
    /// Input: /sys/devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/0003:16C0:05DF.0003/hidraw/hidraw1
    /// Output: hidraw1
    /// </example>
    public static string? ExtractHidrawDevice(string devicePath)
    {
        // Look for /hidraw/hidrawN pattern
        const string marker = "/hidraw/";
        var idx = devicePath.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var hidrawStart = idx + marker.Length;
        // Find end of hidraw name (next / or end of string)
        var hidrawEnd = devicePath.IndexOf('/', hidrawStart);
        if (hidrawEnd < 0)
            hidrawEnd = devicePath.Length;

        return devicePath.Substring(hidrawStart, hidrawEnd - hidrawStart);
    }

    /// <summary>
    /// Compute a stable 8-character hash from a device path.
    /// Uses MD5 for deterministic results across process restarts and platforms.
    /// (String.GetHashCode is randomized per-process in .NET Core)
    /// </summary>
    /// <remarks>
    /// On Linux, HidSharp returns sysfs paths like:
    /// /sys/devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/0003:16C0:05DF.0003/hidraw/hidraw1
    ///
    /// Only the USB port path (e.g., "usb1/1-3") is stable across reboots.
    /// The interface suffix (1-3:1.0), device instance (.0003), and hidraw number all change.
    /// We extract and hash only the stable USB port portion.
    /// </remarks>
    internal static string StableHash(string input)
    {
        var pathToHash = input;

        // On Linux, extract just the USB port path which is stable across reboots
        // Example: /sys/devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/0003:16C0:05DF.0003/hidraw/hidraw1
        // We want just "usb1/1-3" - everything after the port number can change
        var usbIndex = input.IndexOf("/usb", StringComparison.Ordinal);
        if (usbIndex >= 0)
        {
            // Find the USB port path pattern like "usb1/1-3" or "usb2/2-1.4"
            // The port path ends at the colon (1-3:1.0) or at a VID:PID pattern
            var usbPortion = input.Substring(usbIndex + 1); // Skip leading /
            var colonIndex = usbPortion.IndexOf(':');
            if (colonIndex > 0)
            {
                pathToHash = usbPortion.Substring(0, colonIndex);
            }
            else
            {
                // Fallback: strip hidraw suffix at minimum
                var hidrawIndex = usbPortion.IndexOf("/hidraw/", StringComparison.Ordinal);
                pathToHash = hidrawIndex > 0 ? usbPortion.Substring(0, hidrawIndex) : usbPortion;
            }
        }
        else
        {
            // Non-Linux path, just strip hidraw suffix if present
            var hidrawIndex = input.IndexOf("/hidraw/", StringComparison.Ordinal);
            if (hidrawIndex > 0)
            {
                pathToHash = input.Substring(0, hidrawIndex);
            }
        }

        var bytes = Encoding.UTF8.GetBytes(pathToHash);
        var hash = MD5.HashData(bytes);
        // Take first 4 bytes for an 8-char hex string
        return $"{hash[0]:X2}{hash[1]:X2}{hash[2]:X2}{hash[3]:X2}";
    }
}
