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
    /// Uses USB port as primary matching criteria for reliability with identical devices.
    /// </summary>
    /// <param name="busPath">The audio device's bus_path from PulseAudio (e.g., pci-0000:07:1b.0-usb-0:2:1.0).</param>
    /// <param name="vendorId">The USB vendor ID (e.g., "0d8c").</param>
    /// <param name="productId">The USB product ID (e.g., "0102").</param>
    /// <param name="serial">The USB serial number (optional, for fallback matching).</param>
    /// <returns>Path to the input device (e.g., /dev/input/by-id/usb-...-event-if03), or null if not found.</returns>
    public string? FindInputDevice(string? busPath, string? vendorId, string? productId, string? serial = null)
    {
        // Must be a USB device (bus_path contains "usb-")
        if (string.IsNullOrEmpty(busPath) || !busPath.Contains("usb-", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Not a USB device or no bus_path provided: {BusPath}", busPath ?? "(null)");
            return null;
        }

        if (!Directory.Exists(InputByIdPath))
        {
            _logger.LogDebug("Input by-id directory does not exist: {Path}", InputByIdPath);
            return null;
        }

        // Extract target USB port from audio device's bus_path
        var targetUsbPort = ExtractUsbPortFromBusPath(busPath);
        _logger.LogDebug("Looking for HID input device for USB audio: busPath={BusPath}, targetPort={TargetPort}, vendorId={VendorId}",
            busPath, targetUsbPort ?? "(null)", vendorId);

        try
        {
            var entries = Directory.GetFiles(InputByIdPath);

            // PRIMARY: Match by USB port (most reliable, works with duplicate serials)
            if (!string.IsNullOrEmpty(targetUsbPort))
            {
                foreach (var entry in entries)
                {
                    var fileName = Path.GetFileName(entry);

                    // Only consider USB event interfaces
                    if (!InputByIdPattern().IsMatch(fileName))
                        continue;

                    var candidatePort = GetUsbPortForInputDevice(entry);
                    if (candidatePort == targetUsbPort)
                    {
                        _logger.LogInformation("Found HID input device by USB port match: {InputDevice} (port={Port})",
                            entry, candidatePort);
                        return entry;
                    }
                }

                _logger.LogDebug("No HID input device found on USB port {Port}, falling back to vendor/serial match", targetUsbPort);
            }

            // FALLBACK: Match by vendor ID or serial (for cases where port detection fails)
            foreach (var entry in entries)
            {
                var fileName = Path.GetFileName(entry);

                // Only consider USB event interfaces
                if (!InputByIdPattern().IsMatch(fileName))
                    continue;

                var fileNameLower = fileName.ToLowerInvariant();

                // Check vendor ID match
                if (!string.IsNullOrEmpty(vendorId) &&
                    fileNameLower.Contains(vendorId.ToLowerInvariant()))
                {
                    _logger.LogInformation("Found HID input device by vendor ID fallback: {InputDevice} (vendorId={VendorId})",
                        entry, vendorId);
                    return entry;
                }

                // Check serial match
                if (!string.IsNullOrEmpty(serial))
                {
                    var serialNormalized = serial.Replace(" ", "_").Replace("-", "_");
                    if (fileNameLower.Contains(serial.ToLowerInvariant()) ||
                        fileNameLower.Contains(serialNormalized.ToLowerInvariant()))
                    {
                        _logger.LogInformation("Found HID input device by serial fallback: {InputDevice} (serial={Serial})",
                            entry, serial);
                        return entry;
                    }
                }
            }

            _logger.LogDebug("No HID input device found for port={Port}, vendor={VendorId}, serial={Serial}",
                targetUsbPort, vendorId, serial);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error scanning {Path}", InputByIdPath);
        }

        return null;
    }

    /// <summary>
    /// Get the USB port number for an input device by resolving its sysfs path.
    /// Uses shell 'readlink' as .NET symlink resolution doesn't work reliably on Linux.
    /// </summary>
    private string? GetUsbPortForInputDevice(string inputByIdPath)
    {
        try
        {
            // Use readlink to resolve the symlink (more reliable than .NET on Linux)
            var sysfsPath = ResolveInputDeviceSysfsPath(inputByIdPath);
            if (string.IsNullOrEmpty(sysfsPath))
                return null;

            // Extract USB port from sysfs path
            // Example: /sys/devices/.../usb9/9-2/9-2:1.3/... -> port "2"
            var match = SysfsUsbPortPattern().Match(sysfsPath);
            if (match.Success)
            {
                var usbPortFull = match.Groups[1].Value; // e.g., "9-2" or "9-2.1"
                var parts = usbPortFull.Split('-');
                if (parts.Length >= 2)
                {
                    // Return just the port portion (may include hub like "2.1")
                    return parts[1].Split('.')[0]; // "2" from "9-2" or "9-2.1"
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting USB port for {Path}", inputByIdPath);
        }

        return null;
    }

    /// <summary>
    /// Resolve input device symlink to its full sysfs path using shell readlink.
    /// </summary>
    private string? ResolveInputDeviceSysfsPath(string inputByIdPath)
    {
        try
        {
            // First resolve /dev/input/by-id/... -> /dev/input/eventX
            var eventPath = RunReadlink(inputByIdPath);
            if (string.IsNullOrEmpty(eventPath))
                return null;

            // eventPath might be relative (../eventX), make it absolute
            if (!eventPath.StartsWith('/'))
            {
                var dir = Path.GetDirectoryName(inputByIdPath);
                eventPath = Path.GetFullPath(Path.Combine(dir ?? "/dev/input/by-id", eventPath));
            }

            // Get event name (e.g., "event4")
            var eventName = Path.GetFileName(eventPath);

            // Now resolve /sys/class/input/eventX/device -> physical sysfs path
            var sysfsDevicePath = Path.Combine(SysClassInputPath, eventName, "device");
            if (!Directory.Exists(sysfsDevicePath) && !File.Exists(sysfsDevicePath))
            {
                // Try as symlink
                var linkTarget = RunReadlink(sysfsDevicePath);
                if (!string.IsNullOrEmpty(linkTarget))
                {
                    if (!linkTarget.StartsWith('/'))
                    {
                        var dir = Path.GetDirectoryName(sysfsDevicePath);
                        linkTarget = Path.GetFullPath(Path.Combine(dir ?? "", linkTarget));
                    }
                    return linkTarget;
                }
                return null;
            }

            // Resolve the device symlink
            var physicalPath = RunReadlink(sysfsDevicePath);
            if (!string.IsNullOrEmpty(physicalPath))
            {
                if (!physicalPath.StartsWith('/'))
                {
                    var dir = Path.GetDirectoryName(sysfsDevicePath);
                    physicalPath = Path.GetFullPath(Path.Combine(dir ?? "", physicalPath));
                }
                return physicalPath;
            }

            return sysfsDevicePath;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error resolving sysfs path for {Path}", inputByIdPath);
        }

        return null;
    }

    /// <summary>
    /// Run readlink command to resolve a symlink.
    /// </summary>
    private static string? RunReadlink(string path)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "readlink",
                Arguments = $"-f \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(1000);

            return process.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if a device has an available HID input interface.
    /// </summary>
    /// <param name="busPath">The audio device's bus_path from PulseAudio.</param>
    /// <param name="vendorId">The USB vendor ID.</param>
    /// <param name="productId">The USB product ID.</param>
    /// <param name="serial">The USB serial number (optional).</param>
    /// <returns>True if the device has a HID input interface available.</returns>
    public bool HasHidInterface(string? busPath, string? vendorId, string? productId, string? serial = null)
    {
        return FindInputDevice(busPath, vendorId, productId, serial) != null;
    }

    // Keep for potential future use - extracts USB port from bus_path
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

            // Resolve the device symlink to get the physical path
            // Use Directory.ResolveLinkTarget since 'device' is a symlink to a directory
            string? physicalPath = null;
            try
            {
                var linkTarget = Directory.ResolveLinkTarget(sysfsDevicePath, returnFinalTarget: true);
                physicalPath = linkTarget?.FullName;
            }
            catch
            {
                // Fallback: try to read the symlink manually using readlink
            }

            if (string.IsNullOrEmpty(physicalPath))
            {
                // Last resort: use File.ReadLink or just read the sysfs path itself
                try
                {
                    var linkInfo = new FileInfo(sysfsDevicePath);
                    if (linkInfo.LinkTarget != null)
                    {
                        // LinkTarget might be relative, resolve it
                        var parentDir = Path.GetDirectoryName(sysfsDevicePath);
                        physicalPath = Path.GetFullPath(Path.Combine(parentDir ?? "", linkInfo.LinkTarget));
                    }
                }
                catch
                {
                    physicalPath = sysfsDevicePath;
                }
            }

            _logger.LogDebug("Checking input device {Input} -> physical path: {PhysicalPath}",
                inputByIdPath, physicalPath ?? "(null)");

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
