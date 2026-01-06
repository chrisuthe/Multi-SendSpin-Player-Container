using System.Diagnostics;
using System.Text.RegularExpressions;
using MultiRoomAudio.Models;
using Sendspin.SDK.Audio;

namespace MultiRoomAudio.Audio.Alsa;

/// <summary>
/// ALSA audio backend implementation.
/// Provides device enumeration, player creation, and volume control for ALSA devices.
/// </summary>
public partial class AlsaBackend : IBackend
{
    private readonly ILogger<AlsaBackend> _logger;

    public string Name => "alsa";

    public AlsaBackend(ILogger<AlsaBackend> logger)
    {
        _logger = logger;

        // Configure the device enumerator with a logger
        AlsaDeviceEnumerator.SetLogger(logger);
    }

    public IEnumerable<AudioDevice> GetOutputDevices()
    {
        return AlsaDeviceEnumerator.GetOutputDevices();
    }

    public AudioDevice? GetDevice(string deviceId)
    {
        return AlsaDeviceEnumerator.GetDevice(deviceId);
    }

    public AudioDevice? GetDefaultDevice()
    {
        return AlsaDeviceEnumerator.GetDefaultDevice();
    }

    public bool ValidateDevice(string? deviceId, out string? errorMessage)
    {
        return AlsaDeviceEnumerator.ValidateDevice(deviceId, out errorMessage);
    }

    public void RefreshDevices()
    {
        AlsaDeviceEnumerator.RefreshDevices();
    }

    public DeviceCapabilities? GetDeviceCapabilities(string? deviceId)
    {
        var device = deviceId ?? "default";
        _logger.LogDebug("Probing capabilities for ALSA device: {Device}", device);

        var probe = new AlsaCapabilityProbe(_logger);
        return probe.Probe(device);
    }

    public IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory)
    {
        return CreatePlayer(deviceId, loggerFactory, null);
    }

    public IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory, AudioOutputFormat? outputFormat)
    {
        _logger.LogDebug("Creating ALSA player for device: {Device}, format: {Format}",
            deviceId ?? "default",
            outputFormat != null ? $"{outputFormat.SampleRate}Hz/{outputFormat.BitDepth}-bit" : "default");

        return new AlsaPlayer(
            loggerFactory.CreateLogger<AlsaPlayer>(),
            deviceId,
            outputFormat);
    }

    /// <summary>
    /// Sets hardware volume using amixer.
    /// Only works for hardware devices with mixer controls.
    /// Returns false for software-defined devices (caller should use software volume).
    /// </summary>
    public async Task<bool> SetVolumeAsync(string? deviceId, int volume, CancellationToken cancellationToken = default)
    {
        volume = Math.Clamp(volume, 0, 100);

        // Software-defined devices (not starting with hw:) don't have hardware mixers
        if (string.IsNullOrEmpty(deviceId) || !IsHardwareDevice(deviceId))
        {
            _logger.LogDebug("Device {Device} does not support hardware volume control", deviceId ?? "default");
            return false;
        }

        // Extract card number from device name
        var cardNumber = ExtractCardNumber(deviceId);
        if (cardNumber < 0)
        {
            _logger.LogDebug("Could not determine card number for device {Device}", deviceId);
            return false;
        }

        try
        {
            // Use amixer to set volume on the card
            // Try common mixer control names
            var controlNames = new[] { "Master", "PCM", "Speaker", "Headphone" };

            foreach (var control in controlNames)
            {
                var success = await TrySetAmixerVolumeAsync(cardNumber, control, volume, cancellationToken);
                if (success)
                {
                    _logger.LogDebug("Set volume for card {Card} control {Control} to {Volume}%",
                        cardNumber, control, volume);
                    return true;
                }
            }

            _logger.LogDebug("No working mixer control found for card {Card}", cardNumber);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set hardware volume for device {Device}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// Checks if this looks like a hardware device name.
    /// </summary>
    private static bool IsHardwareDevice(string deviceId)
    {
        return deviceId.StartsWith("hw:", StringComparison.OrdinalIgnoreCase) ||
               deviceId.StartsWith("plughw:", StringComparison.OrdinalIgnoreCase) ||
               deviceId.StartsWith("default:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts card number from ALSA device name.
    /// </summary>
    private int ExtractCardNumber(string deviceId)
    {
        // Match patterns like:
        // hw:0,0 -> card 0
        // hw:CARD=MyCard,DEV=0 -> need to look up card name
        // plughw:1,0 -> card 1

        // Simple numeric format: hw:X or hw:X,Y
        var numericMatch = CardNumberRegex().Match(deviceId);
        if (numericMatch.Success)
        {
            return int.Parse(numericMatch.Groups[1].Value);
        }

        // Named format: hw:CARD=Name
        var namedMatch = CardNameRegex().Match(deviceId);
        if (namedMatch.Success)
        {
            var cardName = namedMatch.Groups[1].Value;
            return LookupCardNumber(cardName);
        }

        return -1;
    }

    /// <summary>
    /// Looks up card number by name using aplay -l.
    /// </summary>
    private int LookupCardNumber(string cardName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "aplay",
                Arguments = "-l",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return -1;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // Look for "card N: CardName [Description]"
            var match = Regex.Match(output,
                $@"card\s+(\d+):\s+{Regex.Escape(cardName)}\s+\[",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to look up card number for {CardName}", cardName);
        }

        return -1;
    }

    /// <summary>
    /// Tries to set volume using amixer for a specific control.
    /// </summary>
    private async Task<bool> TrySetAmixerVolumeAsync(int cardNumber, string control, int volume,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "amixer",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(cardNumber.ToString());
            psi.ArgumentList.Add("sset");
            psi.ArgumentList.Add(control);
            psi.ArgumentList.Add($"{volume}%");

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "amixer command failed for card {Card} control {Control}",
                cardNumber, control);
            return false;
        }
    }

    // Regex patterns
    [GeneratedRegex(@"(?:hw|plughw|default):(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CardNumberRegex();

    [GeneratedRegex(@"CARD=([^,\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CardNameRegex();
}
