using System.Text.RegularExpressions;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Device capabilities with source information.
/// </summary>
public record DeviceCapabilitiesWithSource(
    DeviceCapabilities Capabilities,
    CapabilitySource Source
);

/// <summary>
/// Service for querying ALSA hardware capabilities from /proc/asound.
/// Falls back to PulseAudio sink configuration when ALSA data unavailable.
/// </summary>
public partial class AlsaCapabilityService
{
    private readonly ILogger<AlsaCapabilityService> _logger;

    /// <summary>
    /// Base path for ALSA proc filesystem.
    /// Defaults to /host/asound (Docker mount point), falls back to /proc/asound.
    /// </summary>
    private readonly string _asoundPath;

    public AlsaCapabilityService(ILogger<AlsaCapabilityService> logger)
    {
        _logger = logger;

        // Prefer /host/asound (Docker volume mount), fall back to /proc/asound
        if (Directory.Exists("/host/asound"))
        {
            _asoundPath = "/host/asound";
            _logger.LogInformation("ALSA capability service using mounted path: {Path}", _asoundPath);
        }
        else if (Directory.Exists("/proc/asound"))
        {
            _asoundPath = "/proc/asound";
            _logger.LogInformation("ALSA capability service using native path: {Path}", _asoundPath);
        }
        else
        {
            _asoundPath = "";
            _logger.LogWarning("ALSA proc filesystem not available. Device capabilities will use PulseAudio fallback.");
        }
    }

    /// <summary>
    /// Gets hardware capabilities for a device by its ALSA card number.
    /// </summary>
    /// <param name="alsaCardNumber">ALSA card number (from alsa.card property)</param>
    /// <param name="sinkSampleRate">Current PulseAudio sink sample rate (for fallback)</param>
    /// <param name="sinkBitDepth">Current PulseAudio sink bit depth (for fallback)</param>
    /// <param name="sinkChannels">Current PulseAudio sink channel count (for fallback)</param>
    /// <returns>Capabilities with source indicator</returns>
    public DeviceCapabilitiesWithSource? GetCapabilities(
        int alsaCardNumber,
        int sinkSampleRate,
        int? sinkBitDepth,
        int sinkChannels)
    {
        if (string.IsNullOrEmpty(_asoundPath))
        {
            // No ALSA access, use PulseAudio fallback
            return CreatePulseAudioFallback(sinkSampleRate, sinkBitDepth, sinkChannels);
        }

        var cardPath = Path.Combine(_asoundPath, $"card{alsaCardNumber}");
        if (!Directory.Exists(cardPath))
        {
            _logger.LogDebug("ALSA card path not found: {Path}", cardPath);
            return CreatePulseAudioFallback(sinkSampleRate, sinkBitDepth, sinkChannels);
        }

        // Determine card type and parse accordingly
        // Pass sinkChannels so we use actual channel count from PulseAudio sink
        var capabilities = TryParseHdaCodec(cardPath, alsaCardNumber, sinkChannels)
                       ?? TryParseUsbStream(cardPath, alsaCardNumber, sinkChannels);

        if (capabilities != null)
        {
            return new DeviceCapabilitiesWithSource(capabilities, CapabilitySource.Alsa);
        }

        // Fallback for specialty cards (oxygen, Bluetooth, etc.)
        _logger.LogDebug("Card {CardNumber} uses specialty driver, falling back to PulseAudio max config",
            alsaCardNumber);
        return CreatePulseAudioFallback(sinkSampleRate, sinkBitDepth, sinkChannels);
    }

    /// <summary>
    /// Parses HDA codec file for supported rates and bit depths.
    /// </summary>
    private DeviceCapabilities? TryParseHdaCodec(string cardPath, int cardNumber, int sinkChannels)
    {
        var codecPath = Path.Combine(cardPath, "codec#0");
        if (!File.Exists(codecPath))
            return null;

        try
        {
            var content = File.ReadAllText(codecPath);

            // Parse rates from lines like: "rates [0x560]: 44100 48000 96000 192000"
            var rates = new HashSet<int>();
            var ratesMatches = HdaRatesRegex().Matches(content);
            foreach (Match match in ratesMatches)
            {
                var rateValues = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var rate in rateValues)
                {
                    if (int.TryParse(rate, out var rateInt))
                        rates.Add(rateInt);
                }
            }

            // Parse bit depths from lines like: "bits [0x6]: 16 20 24"
            var bitDepths = new HashSet<int>();
            var bitsMatches = HdaBitsRegex().Matches(content);
            foreach (Match match in bitsMatches)
            {
                var bitValues = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var bit in bitValues)
                {
                    if (int.TryParse(bit, out var bitInt))
                        bitDepths.Add(bitInt);
                }
            }

            if (rates.Count == 0 && bitDepths.Count == 0)
            {
                _logger.LogDebug("No rates/bits found in HDA codec for card {CardNumber}", cardNumber);
                return null;
            }

            var sortedRates = rates.OrderBy(r => r).ToArray();
            var sortedBits = bitDepths.OrderBy(b => b).ToArray();

            _logger.LogDebug("Card {CardNumber} HDA capabilities: rates=[{Rates}], bits=[{Bits}]",
                cardNumber, string.Join(",", sortedRates), string.Join(",", sortedBits));

            return new DeviceCapabilities(
                SupportedSampleRates: sortedRates.Length > 0 ? sortedRates : [48000],
                SupportedBitDepths: sortedBits.Length > 0 ? sortedBits : [16],
                MaxChannels: sinkChannels,  // Use actual channel count from PulseAudio sink
                PreferredSampleRate: sortedRates.Length > 0 ? sortedRates[^1] : 48000,
                PreferredBitDepth: sortedBits.Length > 0 ? sortedBits[^1] : 16
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse HDA codec for card {CardNumber}", cardNumber);
            return null;
        }
    }

    /// <summary>
    /// Parses USB stream file for supported rates and formats.
    /// </summary>
    private DeviceCapabilities? TryParseUsbStream(string cardPath, int cardNumber, int sinkChannels)
    {
        var streamPath = Path.Combine(cardPath, "stream0");
        if (!File.Exists(streamPath))
            return null;

        try
        {
            var content = File.ReadAllText(streamPath);

            // Parse rates from lines like: "Rates: 44100, 48000, 88200, 96000"
            var rates = new HashSet<int>();
            var ratesMatches = UsbRatesRegex().Matches(content);
            foreach (Match match in ratesMatches)
            {
                var rateValues = match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var rate in rateValues)
                {
                    if (int.TryParse(rate.Trim(), out var rateInt))
                        rates.Add(rateInt);
                }
            }

            // Parse bit depths from Format lines like: "Format: S16_LE" or "Format: S24_3LE" or "Format: S32_LE"
            var bitDepths = new HashSet<int>();
            var formatMatches = UsbFormatRegex().Matches(content);
            foreach (Match match in formatMatches)
            {
                var format = match.Groups[1].Value.Trim().ToUpperInvariant();
                var bitDepth = ParseUsbFormat(format);
                if (bitDepth.HasValue)
                    bitDepths.Add(bitDepth.Value);
            }

            if (rates.Count == 0 && bitDepths.Count == 0)
            {
                _logger.LogDebug("No rates/formats found in USB stream for card {CardNumber}", cardNumber);
                return null;
            }

            var sortedRates = rates.OrderBy(r => r).ToArray();
            var sortedBits = bitDepths.OrderBy(b => b).ToArray();

            _logger.LogDebug("Card {CardNumber} USB capabilities: rates=[{Rates}], bits=[{Bits}]",
                cardNumber, string.Join(",", sortedRates), string.Join(",", sortedBits));

            return new DeviceCapabilities(
                SupportedSampleRates: sortedRates.Length > 0 ? sortedRates : [48000],
                SupportedBitDepths: sortedBits.Length > 0 ? sortedBits : [16],
                MaxChannels: sinkChannels,  // Use actual channel count from PulseAudio sink
                PreferredSampleRate: sortedRates.Length > 0 ? sortedRates[^1] : 48000,
                PreferredBitDepth: sortedBits.Length > 0 ? sortedBits[^1] : 16
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse USB stream for card {CardNumber}", cardNumber);
            return null;
        }
    }

    /// <summary>
    /// Parses ALSA USB format string to bit depth.
    /// </summary>
    private static int? ParseUsbFormat(string format)
    {
        // Common USB audio formats:
        // S16_LE, S16_BE - 16-bit signed
        // S24_LE, S24_BE - 24-bit signed (packed)
        // S24_3LE, S24_3BE - 24-bit signed (3 bytes)
        // S32_LE, S32_BE - 32-bit signed
        // U8 - 8-bit unsigned

        if (format.StartsWith("S16") || format.StartsWith("U16"))
            return 16;
        if (format.StartsWith("S24") || format.StartsWith("U24"))
            return 24;
        if (format.StartsWith("S32") || format.StartsWith("U32") || format.StartsWith("FLOAT"))
            return 32;
        if (format.StartsWith("U8") || format.StartsWith("S8"))
            return 8;

        return null;
    }

    /// <summary>
    /// Creates fallback capabilities from PulseAudio sink configuration.
    /// These represent the maximum values the hardware supports (post-negotiation).
    /// </summary>
    private DeviceCapabilitiesWithSource? CreatePulseAudioFallback(
        int sinkSampleRate,
        int? sinkBitDepth,
        int sinkChannels)
    {
        var bitDepth = sinkBitDepth ?? 32;

        var capabilities = new DeviceCapabilities(
            SupportedSampleRates: [sinkSampleRate],
            SupportedBitDepths: [bitDepth],
            MaxChannels: sinkChannels,
            PreferredSampleRate: sinkSampleRate,
            PreferredBitDepth: bitDepth
        );

        return new DeviceCapabilitiesWithSource(capabilities, CapabilitySource.PulseAudioMax);
    }

    // Regex for HDA codec parsing
    // Matches: "rates [0x560]: 44100 48000 96000 192000"
    [GeneratedRegex(@"rates\s+\[0x[0-9a-fA-F]+\]:\s*([\d\s]+)", RegexOptions.Multiline)]
    private static partial Regex HdaRatesRegex();

    // Matches: "bits [0x6]: 16 20 24"
    [GeneratedRegex(@"bits\s+\[0x[0-9a-fA-F]+\]:\s*([\d\s]+)", RegexOptions.Multiline)]
    private static partial Regex HdaBitsRegex();

    // Regex for USB stream parsing
    // Matches: "Rates: 44100, 48000, 88200, 96000"
    [GeneratedRegex(@"Rates:\s*([0-9,\s]+)", RegexOptions.Multiline)]
    private static partial Regex UsbRatesRegex();

    // Matches: "Format: S16_LE" or "Format: S24_3LE"
    [GeneratedRegex(@"Format:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex UsbFormatRegex();
}
