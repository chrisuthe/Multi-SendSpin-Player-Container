using System.Diagnostics;
using System.Text.RegularExpressions;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// Enumerates available audio output devices (sinks) using PulseAudio's pactl command.
/// </summary>
public static partial class PulseAudioDeviceEnumerator
{
    private static ILogger? _logger;

    /// <summary>
    /// Configures the logger for device enumeration diagnostics.
    /// </summary>
    public static void SetLogger(ILogger? logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets all available audio output sinks from PulseAudio.
    /// </summary>
    public static IEnumerable<AudioDevice> GetOutputDevices()
    {
        var devices = new List<AudioDevice>();

        try
        {
            // Get default sink name first
            var defaultSink = GetDefaultSinkName();

            // Get all sinks
            var sinksOutput = RunPactl("list sinks");
            if (string.IsNullOrEmpty(sinksOutput))
            {
                _logger?.LogWarning("pactl list sinks returned empty output");
                return devices;
            }

            // Parse sinks
            var sinkBlocks = sinksOutput.Split(new[] { "Sink #" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in sinkBlocks)
            {
                try
                {
                    var device = ParseSinkBlock(block, defaultSink);
                    if (device != null)
                    {
                        devices.Add(device);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to parse sink block");
                }
            }

            _logger?.LogDebug("Found {Count} PulseAudio sinks", devices.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to enumerate PulseAudio sinks");
        }

        return devices;
    }

    /// <summary>
    /// Gets a specific audio device by ID (sink name) or index.
    /// </summary>
    public static AudioDevice? GetDevice(string deviceId)
    {
        // Try to parse as index first
        if (int.TryParse(deviceId, out var index))
        {
            return GetOutputDevices().FirstOrDefault(d => d.Index == index);
        }

        // Search by name (exact match on ID, partial match on Name)
        return GetOutputDevices()
            .FirstOrDefault(d =>
                d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains(deviceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the default audio output sink.
    /// </summary>
    public static AudioDevice? GetDefaultDevice()
    {
        return GetOutputDevices().FirstOrDefault(d => d.IsDefault);
    }

    /// <summary>
    /// Validates that a device ID (sink name) exists and is usable.
    /// </summary>
    public static bool ValidateDevice(string? deviceId, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            // Default device is always valid
            errorMessage = null;
            return true;
        }

        var device = GetDevice(deviceId);
        if (device == null)
        {
            errorMessage = $"PulseAudio sink '{deviceId}' not found. Use /api/devices to list available sinks.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Refreshes device list. For PulseAudio, this is a no-op since pactl always queries live state.
    /// </summary>
    public static void RefreshDevices()
    {
        // No-op: pactl always queries current PulseAudio state
        _logger?.LogDebug("PulseAudio device refresh requested (no-op, pactl queries live state)");
    }

    private static string? GetDefaultSinkName()
    {
        try
        {
            var output = RunPactl("info");
            if (string.IsNullOrEmpty(output))
                return null;

            // Look for "Default Sink: <name>"
            var match = DefaultSinkRegex().Match(output);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to get default sink name");
        }

        return null;
    }

    private static AudioDevice? ParseSinkBlock(string block, string? defaultSink)
    {
        // Extract sink index from start of block
        var indexMatch = SinkIndexRegex().Match(block);
        if (!indexMatch.Success)
            return null;

        var index = int.Parse(indexMatch.Groups[1].Value);

        // Extract sink name
        var nameMatch = SinkNameRegex().Match(block);
        if (!nameMatch.Success)
            return null;

        var sinkName = nameMatch.Groups[1].Value.Trim();

        // Extract description (human-readable name)
        var descMatch = DescriptionRegex().Match(block);
        var description = descMatch.Success
            ? descMatch.Groups[1].Value.Trim()
            : sinkName;

        // Extract sample spec
        var sampleSpecMatch = SampleSpecRegex().Match(block);
        var sampleRate = 48000;
        var channels = 2;

        if (sampleSpecMatch.Success)
        {
            // Format: "s16le 2ch 48000Hz" or "float32le 2ch 44100Hz"
            var specParts = sampleSpecMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in specParts)
            {
                if (part.EndsWith("ch") && int.TryParse(part[..^2], out var ch))
                {
                    channels = ch;
                }
                else if (part.EndsWith("Hz") && int.TryParse(part[..^2], out var rate))
                {
                    sampleRate = rate;
                }
            }
        }

        // Extract stable device identifiers from Properties section
        var identifiers = ParseDeviceIdentifiers(block);

        var isDefault = sinkName.Equals(defaultSink, StringComparison.OrdinalIgnoreCase);

        return new AudioDevice(
            Index: index,
            Id: sinkName,
            Name: description,
            MaxChannels: channels,
            DefaultSampleRate: sampleRate,
            DefaultLowLatencyMs: 50,   // Reasonable default for PulseAudio
            DefaultHighLatencyMs: 200, // Reasonable default for PulseAudio
            IsDefault: isDefault,
            Identifiers: identifiers
        );
    }

    /// <summary>
    /// Extracts stable device identifiers from the Properties section of a pactl sink block.
    /// These identifiers persist across reboots and can be used to re-match devices.
    /// </summary>
    private static DeviceIdentifiers? ParseDeviceIdentifiers(string block)
    {
        var serialMatch = DeviceSerialRegex().Match(block);
        var busPathMatch = DeviceBusPathRegex().Match(block);
        var vendorIdMatch = DeviceVendorIdRegex().Match(block);
        var productIdMatch = DeviceProductIdRegex().Match(block);
        var alsaLongCardNameMatch = AlsaLongCardNameRegex().Match(block);

        // Only create identifiers if we found at least one useful property
        if (!serialMatch.Success && !busPathMatch.Success && !vendorIdMatch.Success &&
            !productIdMatch.Success && !alsaLongCardNameMatch.Success)
        {
            return null;
        }

        return new DeviceIdentifiers(
            Serial: serialMatch.Success ? serialMatch.Groups[1].Value : null,
            BusPath: busPathMatch.Success ? busPathMatch.Groups[1].Value : null,
            VendorId: vendorIdMatch.Success ? vendorIdMatch.Groups[1].Value : null,
            ProductId: productIdMatch.Success ? productIdMatch.Groups[1].Value : null,
            AlsaLongCardName: alsaLongCardNameMatch.Success ? alsaLongCardNameMatch.Groups[1].Value : null
        );
    }

    /// <summary>
    /// Maximum retries for pactl commands when PulseAudio is temporarily unavailable.
    /// </summary>
    private const int MaxPactlRetries = 3;

    /// <summary>
    /// Delay between pactl retry attempts in milliseconds.
    /// </summary>
    private const int PactlRetryDelayMs = 500;

    private static string? RunPactl(string arguments)
    {
        Exception? lastException = null;
        string? lastError = null;

        for (int attempt = 1; attempt <= MaxPactlRetries; attempt++)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pactl",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    lastError = "Failed to start pactl process";
                    continue;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                {
                    lastError = error.Trim();

                    // Check if this is a connection error that might be transient
                    if (error.Contains("Connection refused") ||
                        error.Contains("Connection failure") ||
                        error.Contains("No PulseAudio daemon running"))
                    {
                        if (attempt < MaxPactlRetries)
                        {
                            _logger?.LogDebug(
                                "pactl {Args} failed (attempt {Attempt}/{Max}): {Error}. Retrying...",
                                arguments, attempt, MaxPactlRetries, lastError);
                            Thread.Sleep(PactlRetryDelayMs);
                            continue;
                        }
                    }

                    _logger?.LogWarning("pactl {Args} failed: {Error}", arguments, lastError);
                    return null;
                }

                // Success - return output
                return output;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < MaxPactlRetries)
                {
                    _logger?.LogDebug(ex,
                        "pactl {Args} threw exception (attempt {Attempt}/{Max}). Retrying...",
                        arguments, attempt, MaxPactlRetries);
                    Thread.Sleep(PactlRetryDelayMs);
                }
            }
        }

        // All retries exhausted
        if (lastException != null)
        {
            _logger?.LogError(lastException, "Failed to run pactl {Args} after {Attempts} attempts", arguments, MaxPactlRetries);
        }
        else if (lastError != null)
        {
            _logger?.LogWarning("pactl {Args} failed after {Attempts} attempts: {Error}", arguments, MaxPactlRetries, lastError);
        }

        return null;
    }

    // Regex patterns for parsing pactl output
    [GeneratedRegex(@"^(\d+)", RegexOptions.Multiline)]
    private static partial Regex SinkIndexRegex();

    [GeneratedRegex(@"Name:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SinkNameRegex();

    [GeneratedRegex(@"Description:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex(@"Sample Specification:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SampleSpecRegex();

    [GeneratedRegex(@"Default Sink:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex DefaultSinkRegex();

    // Regex patterns for stable device identifiers (from Properties section)
    [GeneratedRegex(@"device\.serial\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeviceSerialRegex();

    [GeneratedRegex(@"device\.bus_path\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeviceBusPathRegex();

    [GeneratedRegex(@"device\.vendor\.id\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeviceVendorIdRegex();

    [GeneratedRegex(@"device\.product\.id\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeviceProductIdRegex();

    [GeneratedRegex(@"alsa\.long_card_name\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex AlsaLongCardNameRegex();
}
