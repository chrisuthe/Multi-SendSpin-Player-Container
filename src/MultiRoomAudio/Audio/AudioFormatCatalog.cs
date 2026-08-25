using MultiRoomAudio.Models;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Builds the <c>supported_formats</c> list a player advertises in <c>client/hello</c>,
/// derived from the real capabilities of the DAC it plays to.
/// </summary>
/// <remarks>
/// Pure and static so the wire contract can be unit tested without the SDK service graph.
/// Two rules drive everything here:
/// <list type="bullet">
/// <item>Every flac/pcm entry carries an explicit bit depth — the SDK silently maps a null
/// depth to 16, which is what truncated hi-res streams before (#280).</item>
/// <item>Entry 0 is the whole contract — the Music Assistant server picks
/// <c>compatible[0]</c> and holds it for the session, so ordering is the preference.</item>
/// </list>
/// </remarks>
public static class AudioFormatCatalog
{
    /// <summary>Preference string meaning "the device's highest rate and depth".</summary>
    public const string AutoFormatId = "auto";

    /// <summary>Legacy preference string; now resolves to the same anchor as no preference at all.</summary>
    public const string AllFormatsId = "all";

    /// <summary>Sample rate the default anchor prefers — hi-res stays opt-in because buffer RAM scales with it.</summary>
    private const int DefaultSampleRate = 48000;

    /// <summary>Depth cap for the default anchor. 24-bit costs bandwidth but no RAM, so it is safe by default.</summary>
    private const int DefaultMaxBitDepth = 24;

    /// <summary>Rates below this are capture/telephony modes we never want to advertise for music.</summary>
    private const int MinAdvertisedSampleRate = 44100;

    private const int AdvertisedChannels = 2;
    private const int OpusBitrateKbps = 256;

    /// <summary>Used when a device reports no usable capabilities (probe failed, or a virtual sink).</summary>
    private static readonly int[] FallbackSampleRates = [192000, 96000, 48000, 44100];
    private static readonly int[] FallbackBitDepths = [32, 24, 16];

    private static readonly int[] PcmBitDepths = [32, 24, 16];

    /// <summary>FLAC carries 16- or 24-bit samples; a 32-bit-only device is advertised at 24.</summary>
    private static readonly int[] FlacBitDepths = [24, 16];

    /// <summary>
    /// Picks the capabilities to build a device's advertised formats from.
    /// </summary>
    /// <param name="device">The enriched device, whose <c>Capabilities</c> carry ALSA-probed hardware data.</param>
    /// <param name="backendProbe">The backend's own probe, used only when the device has none.</param>
    /// <returns>The best available capabilities, or null when neither source has any.</returns>
    /// <remarks>
    /// The enriched device is the only source that is actually per-device:
    /// <see cref="PulseAudio.PulseAudioBackend.GetDeviceCapabilities"/> returns one fixed
    /// hi-res table for every sink, so building formats from it would advertise 192kHz on a
    /// 48kHz-only DAC. ALSA capabilities are attached during enrichment
    /// (<see cref="Services.DeviceMatchingService"/>), which is why they win here.
    /// </remarks>
    public static DeviceCapabilities? CapabilitiesFor(AudioDevice? device, DeviceCapabilities? backendProbe) =>
        device?.Capabilities ?? backendProbe;

    /// <summary>
    /// Builds the full advertisable format list for a device, best quality first.
    /// </summary>
    /// <param name="capabilities">Probed device capabilities, or null when unavailable.</param>
    /// <returns>flac entries, then pcm entries, then opus — each with an explicit bit depth where the codec has one.</returns>
    public static List<AudioFormat> BuildFormats(DeviceCapabilities? capabilities)
    {
        var rates = ResolveSampleRates(capabilities);
        var depths = ResolveBitDepths(capabilities);

        var formats = new List<AudioFormat>();

        foreach (var rate in rates)
        {
            foreach (var depth in FlacDepthsFor(depths))
            {
                formats.Add(new AudioFormat
                {
                    Codec = "flac",
                    SampleRate = rate,
                    Channels = AdvertisedChannels,
                    BitDepth = depth
                });
            }
        }

        foreach (var rate in rates)
        {
            foreach (var depth in PcmBitDepths.Where(depths.Contains))
            {
                formats.Add(new AudioFormat
                {
                    Codec = "pcm",
                    SampleRate = rate,
                    Channels = AdvertisedChannels,
                    BitDepth = depth
                });
            }
        }

        // Opus is decoded in software and is fixed at 48kHz; bit depth is ignored for it by spec.
        formats.Add(new AudioFormat
        {
            Codec = "opus",
            SampleRate = 48000,
            Channels = AdvertisedChannels,
            Bitrate = OpusBitrateKbps
        });

        return formats;
    }

    /// <summary>
    /// Picks the entry a player with no explicit preference should advertise first:
    /// FLAC at 48kHz (or the nearest rate the device supports) and the device's best depth, capped at 24-bit.
    /// </summary>
    /// <param name="formats">The list from <see cref="BuildFormats"/>.</param>
    /// <param name="capabilities">Probed device capabilities, or null when unavailable.</param>
    /// <returns>The default anchor, never null for a non-empty list.</returns>
    public static AudioFormat ResolveDefault(IReadOnlyList<AudioFormat> formats, DeviceCapabilities? capabilities)
    {
        var rate = NearestSampleRate(ResolveSampleRates(capabilities), DefaultSampleRate);
        var depth = Math.Min(FlacDepthsFor(ResolveBitDepths(capabilities)).Max(), DefaultMaxBitDepth);

        return Match(formats, "flac", rate, depth)
            ?? formats.FirstOrDefault(f => f.Codec == "flac")
            ?? formats[0];
    }

    /// <summary>
    /// Resolves a persisted preference string to one entry of <paramref name="formats"/>.
    /// </summary>
    /// <param name="formats">The list from <see cref="BuildFormats"/>.</param>
    /// <param name="preference">
    /// <c>null</c>/empty or <c>"all"</c> for the default anchor, <c>"auto"</c> for the device's
    /// native best, or <c>codec-rate</c> / <c>codec-rate-depth</c> (e.g. <c>"flac-48000"</c>,
    /// <c>"pcm-96000-24"</c>). A missing depth is resolved from the device.
    /// </param>
    /// <param name="capabilities">Probed device capabilities, or null when unavailable.</param>
    /// <returns>The matching entry, or null when an explicit preference matches nothing the device can do.</returns>
    public static AudioFormat? ResolvePreferred(
        IReadOnlyList<AudioFormat> formats,
        string? preference,
        DeviceCapabilities? capabilities)
    {
        if (formats.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(preference) ||
            preference.Equals(AllFormatsId, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDefault(formats, capabilities);
        }

        if (preference.Equals(AutoFormatId, StringComparison.OrdinalIgnoreCase))
        {
            // Device native: highest rate the device does, at the best depth flac can carry.
            var nativeRate = ResolveSampleRates(capabilities)[0];
            return Match(formats, "flac", nativeRate, FlacDepthsFor(ResolveBitDepths(capabilities)).Max())
                ?? formats.FirstOrDefault(f => f.Codec == "flac");
        }

        var parts = preference.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var sampleRate))
            return null;

        var codec = parts[0].ToLowerInvariant();

        if (parts.Length >= 3 && int.TryParse(parts[2], out var bitDepth))
            return Match(formats, codec, sampleRate, bitDepth);

        // Legacy two-part form ("flac-48000"): the depth comes from the device, best first.
        return formats
            .Where(f => f.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase) && f.SampleRate == sampleRate)
            .OrderByDescending(f => f.BitDepth ?? 0)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns the full list with <paramref name="preferred"/> moved to index 0,
    /// the remainder keeping its descending-quality order.
    /// </summary>
    /// <param name="formats">The list from <see cref="BuildFormats"/>.</param>
    /// <param name="preferred">The entry to advertise first.</param>
    /// <returns>A new, reordered list.</returns>
    public static List<AudioFormat> WithPreferredFirst(IReadOnlyList<AudioFormat> formats, AudioFormat preferred)
    {
        var ordered = new List<AudioFormat>(formats.Count) { preferred };
        ordered.AddRange(formats.Where(f => !ReferenceEquals(f, preferred)));
        return ordered;
    }

    /// <summary>
    /// Renders a format as the preference string persisted in players.yaml.
    /// </summary>
    /// <param name="format">The format to render.</param>
    /// <returns>e.g. <c>"flac-48000-24"</c>, or <c>"opus-48000"</c> for depthless codecs.</returns>
    public static string ToFormatId(AudioFormat format) =>
        format.BitDepth.HasValue
            ? $"{format.Codec}-{format.SampleRate}-{format.BitDepth}"
            : $"{format.Codec}-{format.SampleRate}";

    /// <summary>
    /// Builds the selectable format options for a device, for the player settings dropdown.
    /// </summary>
    /// <param name="capabilities">Probed device capabilities, or null when unavailable.</param>
    /// <returns>"Auto (device native)" first, then every advertisable format, best quality first.</returns>
    public static List<AudioFormatOption> BuildOptions(DeviceCapabilities? capabilities)
    {
        var formats = BuildFormats(capabilities);
        var defaultFormat = ResolveDefault(formats, capabilities);
        var nativeRate = ResolveSampleRates(capabilities)[0];
        var nativeDepth = FlacDepthsFor(ResolveBitDepths(capabilities)).Max();

        var options = new List<AudioFormatOption>
        {
            new(AutoFormatId,
                "Auto (device native)",
                $"Follow the device: FLAC {DescribeRate(nativeRate)} {nativeDepth}-bit")
        };

        foreach (var format in formats)
        {
            var label = format.BitDepth.HasValue
                ? $"{format.Codec.ToUpperInvariant()} {DescribeRate(format.SampleRate)} {format.BitDepth}-bit"
                : $"{format.Codec.ToUpperInvariant()} {DescribeRate(format.SampleRate)}";

            options.Add(new AudioFormatOption(ToFormatId(format), label, DescribeFormat(format, defaultFormat)));
        }

        return options;
    }

    /// <summary>
    /// Gets the preference string a player with no saved format resolves to on this device.
    /// </summary>
    /// <param name="capabilities">Probed device capabilities, or null when unavailable.</param>
    /// <returns>The default anchor's format id.</returns>
    public static string GetDefaultFormatId(DeviceCapabilities? capabilities)
    {
        var formats = BuildFormats(capabilities);
        return ToFormatId(ResolveDefault(formats, capabilities));
    }

    private static AudioFormat? Match(IReadOnlyList<AudioFormat> formats, string codec, int sampleRate, int bitDepth) =>
        formats.FirstOrDefault(f =>
            f.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase) &&
            f.SampleRate == sampleRate &&
            f.BitDepth == bitDepth);

    /// <summary>Device rates, music rates only, best first. Falls back to the static table when the probe gave us nothing.</summary>
    private static int[] ResolveSampleRates(DeviceCapabilities? capabilities)
    {
        var rates = capabilities?.SupportedSampleRates?
            .Where(r => r >= MinAdvertisedSampleRate)
            .Distinct()
            .OrderByDescending(r => r)
            .ToArray();

        return rates is { Length: > 0 } ? rates : FallbackSampleRates;
    }

    /// <summary>Device depths we can actually encode, best first. Falls back to the static table when the probe gave us nothing.</summary>
    private static int[] ResolveBitDepths(DeviceCapabilities? capabilities)
    {
        var depths = capabilities?.SupportedBitDepths?
            .Where(d => PcmBitDepths.Contains(d))
            .Distinct()
            .OrderByDescending(d => d)
            .ToArray();

        return depths is { Length: > 0 } ? depths : FallbackBitDepths;
    }

    /// <summary>
    /// FLAC depths for a device. A device that only reports 32-bit (the PulseAudio float32
    /// fallback) still gets 24-bit FLAC — the alternative is 16 and a repeat of #280.
    /// </summary>
    private static int[] FlacDepthsFor(int[] deviceDepths)
    {
        var supported = FlacBitDepths.Where(deviceDepths.Contains).ToArray();
        return supported.Length > 0 ? supported : [24];
    }

    private static int NearestSampleRate(int[] rates, int target)
    {
        if (rates.Contains(target))
            return target;

        // Ties go to the higher rate — upsampling beats losing detail.
        return rates.OrderBy(r => Math.Abs(r - target)).ThenByDescending(r => r).First();
    }

    private static string DescribeRate(int sampleRate) =>
        sampleRate % 1000 == 0
            ? $"{sampleRate / 1000}kHz"
            : $"{sampleRate / 1000.0:0.#}kHz";

    private static string DescribeFormat(AudioFormat format, AudioFormat defaultFormat)
    {
        var basis = format.Codec switch
        {
            "flac" => "Lossless compressed",
            "pcm" => "Uncompressed",
            "opus" => $"Lossy compressed, {OpusBitrateKbps}kbps",
            _ => format.Codec
        };

        return ReferenceEquals(format, defaultFormat) ? $"{basis} (default)" : basis;
    }
}
