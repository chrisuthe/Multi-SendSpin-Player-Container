using Microsoft.Extensions.Logging;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Audio.Alsa;

/// <summary>
/// Probes ALSA audio devices to detect their capabilities (sample rates, bit depths, channels).
/// Uses ALSA hw_params API to query actual hardware support.
/// </summary>
public class AlsaCapabilityProbe
{
    /// <summary>
    /// Common audio sample rates to test for support.
    /// Ordered from standard to high-resolution rates.
    /// </summary>
    private static readonly uint[] CommonSampleRates =
    {
        44100,  // CD quality
        48000,  // DVD/DAT standard
        88200,  // 2x CD
        96000,  // DVD-Audio, high-res
        176400, // 4x CD
        192000, // High-res audio
    };

    /// <summary>
    /// Formats to test, ordered by preference for audio quality.
    /// We prefer signed little-endian formats as they are most common on x86/ARM.
    /// </summary>
    private static readonly AlsaNative.Format[] FormatsToTest =
    {
        AlsaNative.Format.FLOAT_LE,    // 32-bit float (internal format)
        AlsaNative.Format.S32_LE,      // 32-bit signed
        AlsaNative.Format.S24_LE,      // 24-bit in 4 bytes
        AlsaNative.Format.S24_3LE,     // 24-bit packed (3 bytes)
        AlsaNative.Format.S16_LE,      // 16-bit signed
    };

    private readonly ILogger? _logger;

    public AlsaCapabilityProbe(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Probes a device for its full capabilities.
    /// </summary>
    /// <param name="deviceName">ALSA device name (e.g., "hw:0,0", "plughw:1,0").</param>
    /// <returns>Device capabilities, or null if probe failed.</returns>
    public DeviceCapabilities? Probe(string deviceName)
    {
        var rates = GetSupportedSampleRates(deviceName);
        var formats = GetSupportedFormats(deviceName);
        var (minChannels, maxChannels) = GetChannelRange(deviceName);

        if (rates.Count == 0 && formats.Count == 0)
        {
            _logger?.LogWarning("Failed to probe capabilities for device {Device}", deviceName);
            return null;
        }

        // Convert formats to bit depths
        var bitDepths = formats
            .Select(f => AlsaNative.GetBitDepth(f))
            .Where(b => b > 0)
            .Distinct()
            .OrderByDescending(b => b)
            .ToArray();

        // Determine preferred format (highest quality available)
        var preferredRate = rates.Count > 0 ? rates.Max() : 48000u;
        var preferredBitDepth = bitDepths.Length > 0 ? bitDepths.Max() : 32;

        var capabilities = new DeviceCapabilities(
            SupportedSampleRates: rates.OrderBy(r => r).Select(r => (int)r).ToArray(),
            SupportedBitDepths: bitDepths,
            MaxChannels: (int)maxChannels,
            PreferredSampleRate: (int)preferredRate,
            PreferredBitDepth: preferredBitDepth
        );

        _logger?.LogDebug(
            "Probed device {Device}: rates=[{Rates}], depths=[{Depths}], channels={Channels}",
            deviceName,
            string.Join(",", capabilities.SupportedSampleRates),
            string.Join(",", capabilities.SupportedBitDepths),
            capabilities.MaxChannels);

        return capabilities;
    }

    /// <summary>
    /// Gets all supported sample rates for a device.
    /// </summary>
    public List<uint> GetSupportedSampleRates(string deviceName)
    {
        var supportedRates = new List<uint>();
        var pcm = IntPtr.Zero;
        var hwParams = IntPtr.Zero;

        try
        {
            // Open device in non-blocking mode for quick probe
            var result = AlsaNative.Open(out pcm, deviceName, AlsaNative.StreamType.Playback, 1);
            if (result < 0)
            {
                _logger?.LogDebug("Failed to open device {Device} for probing: {Error}",
                    deviceName, AlsaNative.GetErrorMessage(result));
                return supportedRates;
            }

            // Allocate hw_params
            result = AlsaNative.HwParamsMalloc(out hwParams);
            if (result < 0)
            {
                _logger?.LogDebug("Failed to allocate hw_params: {Error}",
                    AlsaNative.GetErrorMessage(result));
                return supportedRates;
            }

            // Fill with full configuration space
            result = AlsaNative.HwParamsAny(pcm, hwParams);
            if (result < 0)
            {
                _logger?.LogDebug("Failed to get hw_params for {Device}: {Error}",
                    deviceName, AlsaNative.GetErrorMessage(result));
                return supportedRates;
            }

            // Get rate range for quick filtering
            AlsaNative.GetRateMin(hwParams, out var minRate, out _);
            AlsaNative.GetRateMax(hwParams, out var maxRate, out _);

            // Test each common rate within the range
            foreach (var rate in CommonSampleRates)
            {
                if (rate < minRate || rate > maxRate)
                    continue;

                // Test if this exact rate is supported (dir=0 for exact match)
                if (AlsaNative.TestRate(pcm, hwParams, rate, 0) == 0)
                {
                    supportedRates.Add(rate);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Exception probing sample rates for {Device}", deviceName);
        }
        finally
        {
            if (hwParams != IntPtr.Zero)
                AlsaNative.HwParamsFree(hwParams);
            if (pcm != IntPtr.Zero)
                AlsaNative.Close(pcm);
        }

        return supportedRates;
    }

    /// <summary>
    /// Gets all supported formats for a device.
    /// </summary>
    internal List<AlsaNative.Format> GetSupportedFormats(string deviceName)
    {
        var supportedFormats = new List<AlsaNative.Format>();
        var pcm = IntPtr.Zero;
        var hwParams = IntPtr.Zero;
        var formatMask = IntPtr.Zero;

        try
        {
            var result = AlsaNative.Open(out pcm, deviceName, AlsaNative.StreamType.Playback, 1);
            if (result < 0)
                return supportedFormats;

            result = AlsaNative.HwParamsMalloc(out hwParams);
            if (result < 0)
                return supportedFormats;

            result = AlsaNative.HwParamsAny(pcm, hwParams);
            if (result < 0)
                return supportedFormats;

            result = AlsaNative.FormatMaskMalloc(out formatMask);
            if (result < 0)
                return supportedFormats;

            // Get the format mask from hw_params
            AlsaNative.GetFormatMask(hwParams, formatMask);

            // Test each format we care about
            foreach (var format in FormatsToTest)
            {
                if (AlsaNative.FormatMaskTest(formatMask, format) != 0)
                {
                    supportedFormats.Add(format);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Exception probing formats for {Device}", deviceName);
        }
        finally
        {
            if (formatMask != IntPtr.Zero)
                AlsaNative.FormatMaskFree(formatMask);
            if (hwParams != IntPtr.Zero)
                AlsaNative.HwParamsFree(hwParams);
            if (pcm != IntPtr.Zero)
                AlsaNative.Close(pcm);
        }

        return supportedFormats;
    }

    /// <summary>
    /// Gets the channel range supported by a device.
    /// </summary>
    public (uint min, uint max) GetChannelRange(string deviceName)
    {
        var pcm = IntPtr.Zero;
        var hwParams = IntPtr.Zero;

        try
        {
            var result = AlsaNative.Open(out pcm, deviceName, AlsaNative.StreamType.Playback, 1);
            if (result < 0)
                return (2, 2); // Default to stereo

            result = AlsaNative.HwParamsMalloc(out hwParams);
            if (result < 0)
                return (2, 2);

            result = AlsaNative.HwParamsAny(pcm, hwParams);
            if (result < 0)
                return (2, 2);

            AlsaNative.GetChannelsMin(hwParams, out var minChannels);
            AlsaNative.GetChannelsMax(hwParams, out var maxChannels);

            return (minChannels, maxChannels);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Exception probing channels for {Device}", deviceName);
            return (2, 2);
        }
        finally
        {
            if (hwParams != IntPtr.Zero)
                AlsaNative.HwParamsFree(hwParams);
            if (pcm != IntPtr.Zero)
                AlsaNative.Close(pcm);
        }
    }

    /// <summary>
    /// Gets the best ALSA format for a target bit depth.
    /// </summary>
    /// <param name="bitDepth">Target bit depth (16, 24, or 32).</param>
    /// <param name="supportedFormats">List of formats supported by the device.</param>
    /// <returns>Best matching format, or FLOAT_LE as fallback.</returns>
    internal static AlsaNative.Format GetFormatForBitDepth(int bitDepth, IEnumerable<AlsaNative.Format> supportedFormats)
    {
        var formats = supportedFormats.ToHashSet();

        return bitDepth switch
        {
            16 => formats.Contains(AlsaNative.Format.S16_LE) ? AlsaNative.Format.S16_LE : AlsaNative.Format.FLOAT_LE,
            24 => formats.Contains(AlsaNative.Format.S24_LE) ? AlsaNative.Format.S24_LE :
                  formats.Contains(AlsaNative.Format.S24_3LE) ? AlsaNative.Format.S24_3LE :
                  formats.Contains(AlsaNative.Format.S32_LE) ? AlsaNative.Format.S32_LE :
                  AlsaNative.Format.FLOAT_LE,
            32 => formats.Contains(AlsaNative.Format.S32_LE) ? AlsaNative.Format.S32_LE :
                  formats.Contains(AlsaNative.Format.FLOAT_LE) ? AlsaNative.Format.FLOAT_LE :
                  AlsaNative.Format.S32_LE,
            _ => AlsaNative.Format.FLOAT_LE
        };
    }
}
