using System.Text.RegularExpressions;

namespace MultiRoomAudio.Services;

/// <summary>
/// Detects HID input devices for USB audio devices.
/// Maps USB audio devices to their corresponding /dev/input/eventX paths.
/// </summary>
public partial class HidInputDeviceDetector
{
    private readonly ILogger<HidInputDeviceDetector> _logger;

    /// <summary>
    /// Directory containing symlinks to input devices by ID.
    /// These symlinks are stable across reboots (unlike /dev/input/eventX).
    /// </summary>
    private const string InputByIdPath = "/dev/input/by-id";

    /// <summary>
    /// Directory containing input device sysfs entries.
    /// </summary>
    private const string SysClassInputPath = "/sys/class/input";

    /// <summary>
    /// Pattern to extract USB port path from audio device bus_path.
    /// Example: pci-0000:07:1b.0-usb-0:2:1.0 -> extracts "0:2"
    /// </summary>
    [GeneratedRegex(@"usb-(\d+:\d+)", RegexOptions.Compiled)]
    private static partial Regex AudioBusPathPattern();

    /// <summary>
    /// Pattern to extract USB port path from input device by-id symlink.
    /// Example: usb-0d8c_USB_Sound_Device-event-if03 -> we need to resolve and check sysfs
    /// </summary>
    [GeneratedRegex(@"^usb-.*-event-if\d+$", RegexOptions.Compiled)]
    private static partial Regex InputByIdPattern();

    /// <summary>
    /// Pattern to extract USB port from sysfs device path.
    /// Example: /devices/pci0000:00/.../usb9/9-2/9-2:1.3/... -> extracts "9-2"
    /// </summary>
    [GeneratedRegex(@"/usb\d+/(\d+-\d+(?:\.\d+)*)/", RegexOptions.Compiled)]
    private static partial Regex SysfsUsbPortPattern();

    public HidInputDeviceDetector(ILogger<HidInputDeviceDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Find the HID input device path for a USB audio device.
    /// </summary>
    /// <param name="busPath">The audio device's bus_path from PulseAudio (e.g., pci-0000:07:1b.0-usb-0:2:1.0).</param>
    /// <param name="vendorId">The USB vendor ID (e.g., "0d8c").</param>
    /// <param name="productId">The USB product ID (e.g., "0102").</param>
    /// <returns>Path to the input device (e.g., /dev/input/by-id/usb-...-event-if03), or null if not found.</returns>
    public string? FindInputDevice(string? busPath, string? vendorId, string? productId)
    {
        if (string.IsNullOrEmpty(busPath))
        {
            _logger.LogDebug("No bus_path provided, cannot find HID input device");
            return null;
        }

        // Extract USB port from audio device bus_path
        var audioUsbPort = ExtractUsbPortFromBusPath(busPath);
        if (string.IsNullOrEmpty(audioUsbPort))
        {
            _logger.LogDebug("Could not extract USB port from bus_path: {BusPath}", busPath);
            return null;
        }

        _logger.LogDebug("Looking for HID input device matching USB port: {UsbPort} (from bus_path: {BusPath})",
            audioUsbPort, busPath);

        // Method 1: Scan /dev/input/by-id/ for matching devices
        var inputDevice = FindInputDeviceByIdDirectory(audioUsbPort, vendorId, productId);
        if (inputDevice != null)
        {
            _logger.LogInformation("Found HID input device: {InputDevice} for USB port {UsbPort}",
                inputDevice, audioUsbPort);
            return inputDevice;
        }

        _logger.LogDebug("No HID input device found for USB port: {UsbPort}", audioUsbPort);
        return null;
    }

    /// <summary>
    /// Check if a device has an available HID input interface.
    /// </summary>
    /// <param name="busPath">The audio device's bus_path from PulseAudio.</param>
    /// <param name="vendorId">The USB vendor ID.</param>
    /// <param name="productId">The USB product ID.</param>
    /// <returns>True if the device has a HID input interface available.</returns>
    public bool HasHidInterface(string? busPath, string? vendorId, string? productId)
    {
        return FindInputDevice(busPath, vendorId, productId) != null;
    }

    private string? ExtractUsbPortFromBusPath(string busPath)
    {
        // Audio bus_path format: pci-0000:07:1b.0-usb-0:2:1.0
        // We want to extract the USB port portion: "0:2"
        // But sysfs uses a different format: "9-2" (bus-port)
        //
        // The audio bus_path uses format: usb-<bus>:<port>:<config>.<interface>
        // The sysfs path uses format: usb<bus>/<bus>-<port>/

        var match = AudioBusPathPattern().Match(busPath);
        if (match.Success)
        {
            // Got "0:2" format, need to convert to compare with sysfs
            var parts = match.Groups[1].Value.Split(':');
            if (parts.Length >= 2)
            {
                // Return in format that we can match against sysfs paths
                // The sysfs format is "bus-port" like "9-2"
                // We'll match by just the port portion since bus numbers vary
                return parts[1]; // Just the port number like "2"
            }
        }

        // Try alternative extraction - look for the port number
        // Format might also be: pci-...-usb-0:1:1.0 where we want "1"
        var usbIndex = busPath.IndexOf("usb-", StringComparison.Ordinal);
        if (usbIndex >= 0)
        {
            var usbPart = busPath[(usbIndex + 4)..]; // Skip "usb-"
            var colonParts = usbPart.Split(':');
            if (colonParts.Length >= 2)
            {
                return colonParts[1]; // Return port number
            }
        }

        return null;
    }

    private string? FindInputDeviceByIdDirectory(string usbPort, string? vendorId, string? productId)
    {
        if (!Directory.Exists(InputByIdPath))
        {
            _logger.LogDebug("Input by-id directory does not exist: {Path}", InputByIdPath);
            return null;
        }

        try
        {
            var entries = Directory.GetFiles(InputByIdPath);

            foreach (var entry in entries)
            {
                var fileName = Path.GetFileName(entry);

                // Only consider USB event interfaces
                if (!InputByIdPattern().IsMatch(fileName))
                    continue;

                // Check if this input device matches our USB port
                if (InputDeviceMatchesUsbPort(entry, usbPort, vendorId, productId))
                {
                    return entry; // Return the by-id path (stable)
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error scanning {Path}", InputByIdPath);
        }

        return null;
    }

    private bool InputDeviceMatchesUsbPort(string inputByIdPath, string targetUsbPort, string? vendorId, string? productId)
    {
        try
        {
            // Resolve the symlink to get the actual device path
            var target = File.ResolveLinkTarget(inputByIdPath, returnFinalTarget: true);
            if (target == null)
                return false;

            var targetPath = target.FullName;

            // Get the sysfs path for this input device
            // /dev/input/eventX -> /sys/class/input/eventX/device
            var eventName = Path.GetFileName(targetPath);
            var sysfsDevicePath = Path.Combine(SysClassInputPath, eventName, "device");

            if (!Directory.Exists(sysfsDevicePath))
            {
                _logger.LogDebug("Sysfs device path not found: {Path}", sysfsDevicePath);
                return false;
            }

            // Read the uevent file or follow device symlink to get USB path
            var deviceLink = new DirectoryInfo(sysfsDevicePath);
            var physicalPath = deviceLink.ResolveLinkTarget(returnFinalTarget: true)?.FullName;

            if (physicalPath == null)
            {
                // Try reading from the symlink directly
                physicalPath = sysfsDevicePath;
            }

            _logger.LogDebug("Checking input device {Input} -> physical path: {PhysicalPath}",
                inputByIdPath, physicalPath);

            // Extract USB port from sysfs path
            var sysfsMatch = SysfsUsbPortPattern().Match(physicalPath);
            if (sysfsMatch.Success)
            {
                var inputUsbPort = sysfsMatch.Groups[1].Value;

                // The input USB port is like "9-2" and we're matching against "2" (just port)
                // Check if the port portion matches
                var inputPortParts = inputUsbPort.Split('-');
                if (inputPortParts.Length >= 2)
                {
                    var inputPort = inputPortParts[1].Split('.')[0]; // Handle "2.1" -> "2"

                    if (inputPort == targetUsbPort)
                    {
                        _logger.LogDebug("USB port match: input {InputPort} matches target {TargetPort}",
                            inputUsbPort, targetUsbPort);
                        return true;
                    }
                }
            }

            // Alternative: check if vendor/product ID matches (from the by-id filename)
            if (!string.IsNullOrEmpty(vendorId) && !string.IsNullOrEmpty(productId))
            {
                var fileName = Path.GetFileName(inputByIdPath).ToLowerInvariant();
                if (fileName.Contains(vendorId.ToLowerInvariant()) ||
                    fileName.Contains(productId.ToLowerInvariant()))
                {
                    // Also verify USB port range if possible
                    if (sysfsMatch.Success)
                    {
                        var inputUsbPort = sysfsMatch.Groups[1].Value;
                        var inputPortParts = inputUsbPort.Split('-');
                        if (inputPortParts.Length >= 2)
                        {
                            var inputPort = inputPortParts[1].Split('.')[0];
                            if (inputPort == targetUsbPort)
                            {
                                _logger.LogDebug("Vendor/Product ID and USB port match for {Input}",
                                    inputByIdPath);
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking input device {Path}", inputByIdPath);
        }

        return false;
    }
}
