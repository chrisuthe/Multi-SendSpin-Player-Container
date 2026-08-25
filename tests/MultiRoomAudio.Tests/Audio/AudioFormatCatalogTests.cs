using MultiRoomAudio.Audio;
using MultiRoomAudio.Models;
using Sendspin.SDK.Models;
using Xunit;

namespace MultiRoomAudio.Tests.Audio;

/// <summary>
/// Covers the wire contract of <c>supported_formats</c>: every flac/pcm entry carries an
/// explicit bit depth, the list is derived from the device, and entry 0 is the preference.
/// </summary>
public class AudioFormatCatalogTests
{
    private static DeviceCapabilities Caps(int[] rates, int[] depths) =>
        new(
            SupportedSampleRates: rates,
            SupportedBitDepths: depths,
            MaxChannels: 2,
            PreferredSampleRate: rates[^1],
            PreferredBitDepth: depths[^1]);

    private static DeviceCapabilities HiResDac() =>
        Caps([44100, 48000, 96000, 192000], [16, 24, 32]);

    private static DeviceCapabilities BasicDac() =>
        Caps([44100, 48000], [16]);

    private static List<AudioFormat> Advertise(DeviceCapabilities? caps, string? preference)
    {
        var all = AudioFormatCatalog.BuildFormats(caps);
        var preferred = AudioFormatCatalog.ResolvePreferred(all, preference, caps)
                        ?? AudioFormatCatalog.ResolveDefault(all, caps);
        return AudioFormatCatalog.WithPreferredFirst(all, preferred);
    }

    private static AudioDevice Device(DeviceCapabilities? capabilities) =>
        new(
            Index: 0,
            Id: "alsa_output.test",
            Name: "Test DAC",
            MaxChannels: 2,
            DefaultSampleRate: 48000,
            DefaultLowLatencyMs: 20,
            DefaultHighLatencyMs: 100,
            IsDefault: false,
            Capabilities: capabilities);

    /// <summary>The fixed table PulseAudioBackend returns for every sink, regardless of device.</summary>
    private static DeviceCapabilities BackendProbe() =>
        Caps([44100, 48000, 88200, 96000, 176400, 192000], [16, 24, 32]);

    // --- Capability source ------------------------------------------------------------------

    [Fact]
    public void CapabilitiesFor_PrefersTheDevicesOwnCapabilitiesOverTheBackendProbe()
    {
        // The regression this guards: building formats from the backend probe advertises
        // 192kHz on every sink, because PulseAudioBackend returns one fixed table for all of them.
        var dac = Caps([48000], [16, 24]);

        var resolved = AudioFormatCatalog.CapabilitiesFor(Device(dac), BackendProbe());

        Assert.Same(dac, resolved);
    }

    [Fact]
    public void CapabilitiesFor_A48kOnlyDeviceNeverAdvertisesHiResEvenWhenTheProbeClaimsIt()
    {
        var resolved = AudioFormatCatalog.CapabilitiesFor(Device(Caps([48000], [16, 24])), BackendProbe());

        var formats = AudioFormatCatalog.BuildFormats(resolved);

        Assert.All(formats, f => Assert.Equal(48000, f.SampleRate));
        Assert.DoesNotContain(formats, f => f.SampleRate is 88200 or 96000 or 176400 or 192000);
    }

    [Fact]
    public void CapabilitiesFor_FallsBackToTheBackendProbeWhenTheDeviceHasNone()
    {
        var probe = BackendProbe();

        Assert.Same(probe, AudioFormatCatalog.CapabilitiesFor(Device(null), probe));
        Assert.Same(probe, AudioFormatCatalog.CapabilitiesFor(null, probe));
    }

    [Fact]
    public void CapabilitiesFor_ReturnsNullWhenNeitherSourceHasCapabilities()
    {
        Assert.Null(AudioFormatCatalog.CapabilitiesFor(null, null));
    }

    // --- Explicit bit depth on every entry -------------------------------------------------

    [Fact]
    public void BuildFormats_GivesEveryFlacAndPcmEntryAnExplicitBitDepth()
    {
        foreach (var caps in new DeviceCapabilities?[] { HiResDac(), BasicDac(), null })
        {
            var formats = AudioFormatCatalog.BuildFormats(caps);

            Assert.All(
                formats.Where(f => f.Codec is "flac" or "pcm"),
                f => Assert.True(f.BitDepth.HasValue, $"{f.Codec} {f.SampleRate} has no bit depth"));
        }
    }

    [Fact]
    public void BuildFormats_LeavesOpusWithoutABitDepth()
    {
        var opus = AudioFormatCatalog.BuildFormats(HiResDac()).Single(f => f.Codec == "opus");

        Assert.Null(opus.BitDepth);
        Assert.Equal(48000, opus.SampleRate);
    }

    // --- Per-device derivation -------------------------------------------------------------

    [Fact]
    public void BuildFormats_NeverAdvertisesARateTheDeviceCannotDo()
    {
        var formats = AudioFormatCatalog.BuildFormats(Caps([48000], [16, 24]));

        Assert.All(
            formats.Where(f => f.Codec != "opus"),
            f => Assert.Equal(48000, f.SampleRate));
        Assert.DoesNotContain(formats, f => f.SampleRate is 96000 or 192000);
    }

    [Fact]
    public void BuildFormats_NeverAdvertisesADepthTheDeviceCannotDo()
    {
        var formats = AudioFormatCatalog.BuildFormats(Caps([48000, 96000], [16]));

        Assert.All(
            formats.Where(f => f.BitDepth.HasValue),
            f => Assert.Equal(16, f.BitDepth));
    }

    [Fact]
    public void BuildFormats_DropsSubMusicRates()
    {
        var formats = AudioFormatCatalog.BuildFormats(Caps([8000, 16000, 32000, 48000], [16]));

        Assert.All(formats, f => Assert.True(f.SampleRate >= 44100));
    }

    [Fact]
    public void BuildFormats_DiffersBetweenDevicesWithDifferentCapabilities()
    {
        var hiRes = AudioFormatCatalog.BuildFormats(HiResDac()).Select(AudioFormatCatalog.ToFormatId);
        var basic = AudioFormatCatalog.BuildFormats(BasicDac()).Select(AudioFormatCatalog.ToFormatId);

        Assert.NotEqual(hiRes, basic);
    }

    [Fact]
    public void BuildFormats_AdvertisesFlacAt24BitWhenTheDeviceOnlyReports32()
    {
        // The PulseAudio float32 fallback: FLAC cannot carry 32-bit, and 16 would re-truncate.
        var formats = AudioFormatCatalog.BuildFormats(Caps([48000], [32]));

        Assert.All(
            formats.Where(f => f.Codec == "flac"),
            f => Assert.Equal(24, f.BitDepth));
        Assert.Contains(formats, f => f.Codec == "pcm" && f.BitDepth == 32);
    }

    [Fact]
    public void BuildFormats_FallsBackToAValidListWhenCapabilitiesAreUnavailable()
    {
        var formats = AudioFormatCatalog.BuildFormats(null);

        Assert.NotEmpty(formats);
        Assert.All(
            formats.Where(f => f.Codec is "flac" or "pcm"),
            f => Assert.True(f.BitDepth.HasValue));
        Assert.Contains(formats, f => f.Codec == "flac" && f.SampleRate == 48000);
    }

    // --- Default anchor --------------------------------------------------------------------

    [Fact]
    public void ResolveDefault_AnchorsOnFlac48kAtTheDevicesBestDepthCappedAt24()
    {
        var caps = HiResDac();
        var entry0 = Advertise(caps, null)[0];

        Assert.Equal("flac", entry0.Codec);
        Assert.Equal(48000, entry0.SampleRate);
        Assert.Equal(24, entry0.BitDepth);
    }

    [Fact]
    public void ResolveDefault_DoesNotUpgradeA16BitDeviceTo24()
    {
        var entry0 = Advertise(BasicDac(), null)[0];

        Assert.Equal(16, entry0.BitDepth);
    }

    [Fact]
    public void ResolveDefault_NeverAutoUpgradesTheSampleRateToHiRes()
    {
        var entry0 = Advertise(HiResDac(), null)[0];

        Assert.Equal(48000, entry0.SampleRate);
    }

    [Fact]
    public void ResolveDefault_AnchorsOnTheNearestRateWhenTheDeviceHasNo48k()
    {
        var entry0 = Advertise(Caps([44100, 88200, 176400], [16, 24]), null)[0];

        Assert.Equal(44100, entry0.SampleRate);
        Assert.Equal(24, entry0.BitDepth);
    }

    [Fact]
    public void GetDefaultFormatId_MatchesTheAnchorItResolves()
    {
        var caps = HiResDac();

        Assert.Equal("flac-48000-24", AudioFormatCatalog.GetDefaultFormatId(caps));
        Assert.Equal("flac-48000-16", AudioFormatCatalog.GetDefaultFormatId(BasicDac()));
    }

    // --- Preference parsing and ordering ---------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("ALL")]
    public void ResolvePreferred_TreatsNullEmptyAndAllAsTheDefaultAnchor(string? preference)
    {
        var caps = HiResDac();
        var all = AudioFormatCatalog.BuildFormats(caps);

        var preferred = AudioFormatCatalog.ResolvePreferred(all, preference, caps);

        Assert.Same(AudioFormatCatalog.ResolveDefault(all, caps), preferred);
    }

    [Fact]
    public void ResolvePreferred_ResolvesTheLegacyTwoPartFormAgainstTheDevice()
    {
        // "flac-48000" predates bit depths in the string; the device supplies the depth.
        var entry0 = Advertise(HiResDac(), "flac-48000")[0];

        Assert.Equal("flac", entry0.Codec);
        Assert.Equal(48000, entry0.SampleRate);
        Assert.Equal(24, entry0.BitDepth);
    }

    [Fact]
    public void ResolvePreferred_ResolvesTheLegacyThreePartPcmForm()
    {
        var entry0 = Advertise(HiResDac(), "pcm-96000-24")[0];

        Assert.Equal("pcm", entry0.Codec);
        Assert.Equal(96000, entry0.SampleRate);
        Assert.Equal(24, entry0.BitDepth);
    }

    [Fact]
    public void ResolvePreferred_HonoursAnExplicitFlacBitDepth()
    {
        var entry0 = Advertise(HiResDac(), "flac-96000-16")[0];

        Assert.Equal("flac", entry0.Codec);
        Assert.Equal(96000, entry0.SampleRate);
        Assert.Equal(16, entry0.BitDepth);
    }

    [Fact]
    public void ResolvePreferred_AutoAnchorsOnTheDevicesNativeBest()
    {
        var entry0 = Advertise(HiResDac(), AudioFormatCatalog.AutoFormatId)[0];

        Assert.Equal("flac", entry0.Codec);
        Assert.Equal(192000, entry0.SampleRate);
        Assert.Equal(24, entry0.BitDepth);
    }

    [Theory]
    [InlineData("flac-192000")]      // rate the device cannot do
    [InlineData("pcm-48000-24")]     // depth the device cannot do
    [InlineData("nonsense")]
    [InlineData("flac-notanumber")]
    public void ResolvePreferred_ReturnsNullWhenNothingMatches(string preference)
    {
        var caps = BasicDac();
        var all = AudioFormatCatalog.BuildFormats(caps);

        Assert.Null(AudioFormatCatalog.ResolvePreferred(all, preference, caps));
    }

    [Fact]
    public void Advertise_FallsBackToTheDefaultWhenThePreferenceIsUnreachable()
    {
        var entry0 = Advertise(BasicDac(), "flac-192000")[0];

        Assert.Equal(48000, entry0.SampleRate);
        Assert.Equal(16, entry0.BitDepth);
    }

    [Fact]
    public void WithPreferredFirst_KeepsTheWholeListAndPreservesTheTailOrder()
    {
        var caps = HiResDac();
        var all = AudioFormatCatalog.BuildFormats(caps);
        var preferred = all.Single(f => f.Codec == "pcm" && f.SampleRate == 96000 && f.BitDepth == 24);

        var ordered = AudioFormatCatalog.WithPreferredFirst(all, preferred);

        Assert.Equal(all.Count, ordered.Count);
        Assert.Same(preferred, ordered[0]);
        Assert.Equal(all.Where(f => !ReferenceEquals(f, preferred)), ordered.Skip(1));
    }

    [Fact]
    public void Advertise_ReturnsTheWholeListNotASingleEntry()
    {
        var formats = Advertise(HiResDac(), "flac-48000");

        Assert.True(formats.Count > 1, "supported_formats must offer MA more than one option");
        Assert.Contains(formats, f => f.Codec == "opus");
        Assert.Contains(formats, f => f.SampleRate == 192000);
    }

    [Fact]
    public void BuildFormats_OrdersFlacBeforePcmAndRatesDescending()
    {
        var formats = AudioFormatCatalog.BuildFormats(HiResDac());
        var codecs = formats.Select(f => f.Codec).ToList();

        Assert.Equal(0, codecs.IndexOf("flac"));
        Assert.True(codecs.IndexOf("pcm") > codecs.LastIndexOf("flac"));
        Assert.Equal("opus", codecs[^1]);

        var flacRates = formats.Where(f => f.Codec == "flac").Select(f => f.SampleRate).ToList();
        Assert.Equal(flacRates.OrderByDescending(r => r), flacRates);
    }

    // --- Format ids and options ------------------------------------------------------------

    [Fact]
    public void ToFormatId_RoundTripsThroughResolvePreferred()
    {
        var caps = HiResDac();
        var all = AudioFormatCatalog.BuildFormats(caps);

        foreach (var format in all)
        {
            var resolved = AudioFormatCatalog.ResolvePreferred(all, AudioFormatCatalog.ToFormatId(format), caps);
            Assert.Same(format, resolved);
        }
    }

    [Fact]
    public void BuildOptions_OffersAutoPlusOnlyWhatTheDeviceCanDo()
    {
        var caps = BasicDac();
        var options = AudioFormatCatalog.BuildOptions(caps);

        Assert.Equal(AudioFormatCatalog.AutoFormatId, options[0].Id);
        Assert.DoesNotContain(options, o => o.Id.Contains("192000"));
        Assert.DoesNotContain(options, o => o.Id.EndsWith("-24"));

        var ids = options.Skip(1).Select(o => o.Id);
        Assert.Equal(AudioFormatCatalog.BuildFormats(caps).Select(AudioFormatCatalog.ToFormatId), ids);
    }

    [Fact]
    public void BuildOptions_MarksTheDefaultAnchor()
    {
        var caps = HiResDac();
        var options = AudioFormatCatalog.BuildOptions(caps);

        var marked = options.Where(o => o.Description.Contains("(default)")).ToList();

        Assert.Single(marked);
        Assert.Equal(AudioFormatCatalog.GetDefaultFormatId(caps), marked[0].Id);
    }
}
