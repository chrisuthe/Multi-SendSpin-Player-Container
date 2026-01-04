using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// Runs ALSA (amixer) and PulseAudio (pactl) commands for volume control.
/// Provides environment-aware volume management.
/// </summary>
public class AlsaCommandRunner
{
    private readonly ILogger<AlsaCommandRunner> _logger;
    private readonly bool _usePulse;

    /// <summary>
    /// Pattern for valid ALSA device strings.
    /// Matches: hw:X,Y, hw:X, plughw:X,Y, default, sysdefault:CARD=X, or simple alphanumeric names.
    /// </summary>
    private static readonly Regex ValidDevicePattern = new(
        @"^(hw:\d+,\d+|hw:\d+|plughw:\d+,\d+|plughw:\d+|default|sysdefault:CARD=[a-zA-Z0-9_]+|[a-zA-Z0-9_\-]+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Characters that are dangerous in shell commands and must be rejected.
    /// </summary>
    private static readonly char[] DangerousChars = { ';', '&', '|', '$', '`', '(', ')', '{', '}', '[', ']', '<', '>', '!', '\\', '"', '\'', '\n', '\r', '\0' };

    public AlsaCommandRunner(ILogger<AlsaCommandRunner> logger, bool usePulseAudio = false)
    {
        _logger = logger;
        _usePulse = usePulseAudio;
    }

    /// <summary>
    /// Validates a device string to prevent command injection.
    /// </summary>
    /// <param name="device">The device string to validate.</param>
    /// <param name="errorMessage">Error message if validation fails.</param>
    /// <returns>True if the device string is safe to use.</returns>
    public static bool ValidateDeviceString(string? device, out string? errorMessage)
    {
        errorMessage = null;

        // Null or empty device is allowed (will use default)
        if (string.IsNullOrWhiteSpace(device))
        {
            return true;
        }

        // Check for dangerous shell metacharacters
        if (device.IndexOfAny(DangerousChars) >= 0)
        {
            errorMessage = $"Device string contains invalid characters. Only alphanumeric characters, hyphens, underscores, colons, and commas are allowed.";
            return false;
        }

        // Check maximum length (prevent buffer overflow attempts)
        if (device.Length > 100)
        {
            errorMessage = "Device string exceeds maximum length of 100 characters.";
            return false;
        }

        // Validate against whitelist pattern
        if (!ValidDevicePattern.IsMatch(device))
        {
            errorMessage = $"Device string '{device}' does not match expected format. Expected formats: hw:X,Y, plughw:X,Y, default, or simple alphanumeric name.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Get current volume as percentage (0-100).
    /// </summary>
    public async Task<int?> GetVolumeAsync(string device, CancellationToken cancellationToken = default)
    {
        // Validate device string to prevent command injection
        if (!ValidateDeviceString(device, out var validationError))
        {
            _logger.LogWarning("Invalid device string rejected: {Error}", validationError);
            return null;
        }

        try
        {
            if (_usePulse)
            {
                return await GetPulseVolumeAsync(device, cancellationToken);
            }
            return await GetAlsaVolumeAsync(device, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get volume for {Device}", device);
            return null;
        }
    }

    /// <summary>
    /// Set volume as percentage (0-100).
    /// </summary>
    public async Task<bool> SetVolumeAsync(string device, int volume, CancellationToken cancellationToken = default)
    {
        // Validate device string to prevent command injection
        if (!ValidateDeviceString(device, out var validationError))
        {
            _logger.LogWarning("Invalid device string rejected: {Error}", validationError);
            return false;
        }

        volume = Math.Clamp(volume, 0, 100);

        try
        {
            if (_usePulse)
            {
                return await SetPulseVolumeAsync(device, volume, cancellationToken);
            }
            return await SetAlsaVolumeAsync(device, volume, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set volume for {Device} to {Volume}", device, volume);
            return false;
        }
    }

    /// <summary>
    /// List available ALSA devices.
    /// </summary>
    public async Task<List<string>> ListAlsaDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<string>();

        try
        {
            // Use aplay -L to list all devices
            var result = await RunCommandAsync("aplay", "-L", cancellationToken);
            if (result.ExitCode != 0)
            {
                _logger.LogWarning("aplay -L returned non-zero exit code: {ExitCode}", result.ExitCode);
                return devices;
            }

            // Parse output - each non-indented line is a device name
            foreach (var line in result.Output.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith(" ") && !line.StartsWith("\t"))
                {
                    devices.Add(line.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list ALSA devices");
        }

        return devices;
    }

    /// <summary>
    /// Get ALSA hardware devices (hw:X,Y format).
    /// </summary>
    public async Task<List<AlsaHardwareDevice>> ListAlsaHardwareAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<AlsaHardwareDevice>();

        try
        {
            // Parse /proc/asound/cards
            var result = await RunCommandAsync("cat", "/proc/asound/cards", cancellationToken);
            if (result.ExitCode != 0)
                return devices;

            // Pattern: " 0 [USB            ]: USB-Audio - USB Audio Device"
            var cardPattern = new Regex(@"^\s*(\d+)\s+\[([^\]]+)\]:\s+(.+)$", RegexOptions.Multiline);
            foreach (Match match in cardPattern.Matches(result.Output))
            {
                var cardNum = int.Parse(match.Groups[1].Value);
                var cardId = match.Groups[2].Value.Trim();
                var cardName = match.Groups[3].Value.Trim();

                // Get subdevices for this card
                var subResult = await RunCommandAsync("cat", $"/proc/asound/card{cardNum}/pcm0p/info", cancellationToken);
                var subdevice = 0;

                if (subResult.ExitCode == 0)
                {
                    // Parse subdevice info if available
                    var subMatch = Regex.Match(subResult.Output, @"subdevice\s*#(\d+)");
                    if (subMatch.Success)
                        subdevice = int.Parse(subMatch.Groups[1].Value);
                }

                devices.Add(new AlsaHardwareDevice
                {
                    Card = cardNum,
                    Device = 0,
                    Subdevice = subdevice,
                    Id = cardId,
                    Name = cardName,
                    HwId = $"hw:{cardNum},0"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list ALSA hardware devices");
        }

        return devices;
    }

    private async Task<int?> GetAlsaVolumeAsync(string device, CancellationToken cancellationToken)
    {
        // Extract card number from device (hw:0,0 -> 0)
        var cardMatch = Regex.Match(device, @"hw:(\d+)");
        var card = cardMatch.Success ? $"-c {cardMatch.Groups[1].Value}" : "";

        var result = await RunCommandAsync("amixer", $"{card} sget Master", cancellationToken);
        if (result.ExitCode != 0)
        {
            // Try PCM control if Master doesn't exist
            result = await RunCommandAsync("amixer", $"{card} sget PCM", cancellationToken);
            if (result.ExitCode != 0)
                return null;
        }

        // Parse output like "[75%]"
        var match = Regex.Match(result.Output, @"\[(\d+)%\]");
        if (match.Success)
        {
            return int.Parse(match.Groups[1].Value);
        }

        return null;
    }

    private async Task<bool> SetAlsaVolumeAsync(string device, int volume, CancellationToken cancellationToken)
    {
        // Extract card number from device
        var cardMatch = Regex.Match(device, @"hw:(\d+)");
        var card = cardMatch.Success ? $"-c {cardMatch.Groups[1].Value}" : "";

        var result = await RunCommandAsync("amixer", $"{card} sset Master {volume}%", cancellationToken);
        if (result.ExitCode != 0)
        {
            // Try PCM control if Master doesn't exist
            result = await RunCommandAsync("amixer", $"{card} sset PCM {volume}%", cancellationToken);
        }

        return result.ExitCode == 0;
    }

    private async Task<int?> GetPulseVolumeAsync(string device, CancellationToken cancellationToken)
    {
        // Get default sink volume
        var result = await RunCommandAsync("pactl", "get-sink-volume @DEFAULT_SINK@", cancellationToken);
        if (result.ExitCode != 0)
            return null;

        // Parse output like "front-left: 65536 / 100%"
        var match = Regex.Match(result.Output, @"(\d+)%");
        if (match.Success)
        {
            return int.Parse(match.Groups[1].Value);
        }

        return null;
    }

    private async Task<bool> SetPulseVolumeAsync(string device, int volume, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("pactl", $"set-sink-volume @DEFAULT_SINK@ {volume}%", cancellationToken);
        return result.ExitCode == 0;
    }

    private async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command,
        string arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Command failed: {Command} {Args}", command, arguments);
            return (-1, string.Empty);
        }
    }
}

public class AlsaHardwareDevice
{
    public int Card { get; set; }
    public int Device { get; set; }
    public int Subdevice { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HwId { get; set; } = string.Empty;
}
