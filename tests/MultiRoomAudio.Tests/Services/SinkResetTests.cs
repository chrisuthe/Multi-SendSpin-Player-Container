using Microsoft.Extensions.Logging;
using MultiRoomAudio.Audio.PulseAudio;
using MultiRoomAudio.Services;
using Xunit;

namespace MultiRoomAudio.Tests.Services;

/// <summary>
/// Tests for <see cref="SinkResetService"/>, the startup suspend/resume workaround for cards that
/// open on the broken ALSA mmap+timer path (#281). The fixture is the <c>pactl list sinks short</c>
/// output pasted in that issue: five hardware sinks off the X-Fi host's cards, seven remap sinks
/// layered on top.
/// </summary>
public class SinkResetTests
{
    private const string Issue281SinksShort =
        "0\talsa_output.pci-0000_01_00.0.iec958-stereo\tmodule-alsa-card.c\ts16le 2ch 48000Hz\tIDLE\n" +
        "1\talsa_output.pci-0000_04_00.0.analog-surround-71\tmodule-alsa-card.c\ts32le 8ch 96000Hz\tRUNNING\n" +
        "2\talsa_output.usb-AudioQuest_AudioQuest_DragonFly_Black_v1.5_AQDFBL0120005724-00.iec958-stereo\tmodule-alsa-card.c\ts24le 2ch 96000Hz\tIDLE\n" +
        "3\talsa_output.usb-Generic_ELEGIANT_SR030_20170726905959-00.analog-stereo\tmodule-alsa-card.c\ts16le 2ch 48000Hz\tIDLE\n" +
        "4\talsa_output.pci-0000_08_00.6.analog-surround-51\tmodule-alsa-card.c\ts32le 6ch 96000Hz\tRUNNING\n" +
        "5\tBA_790\tmodule-remap-sink.c\ts32le 2ch 96000Hz\tRUNNING\n" +
        "6\tYamaha_Analog\tmodule-remap-sink.c\ts32le 2ch 96000Hz\tRUNNING\n" +
        "7\tBack_Yard_Pyle_Speakers\tmodule-remap-sink.c\ts32le 2ch 96000Hz\tRUNNING\n" +
        "8\tBasement_Front_Room\tmodule-remap-sink.c\ts32le 2ch 96000Hz\tRUNNING\n" +
        "9\tDining_Room_iHome\tmodule-remap-sink.c\ts32le 2ch 96000Hz\tRUNNING\n" +
        "10\tBasement_Back_Room\tmodule-remap-sink.c\ts32le 2ch 96000Hz\tRUNNING\n" +
        "11\tWorkshop\tmodule-remap-sink.c\ts32le 2ch 96000Hz\tRUNNING\n";

    private const string XfiSink = "alsa_output.pci-0000_04_00.0.analog-surround-71";

    /// <summary>
    /// Answers the sink listing from a fixture and records every pactl call, with hooks for making
    /// individual commands fail or throw.
    /// </summary>
    private sealed class FakePactl
    {
        public string Listing { get; set; } = Issue281SinksShort;

        /// <summary>Returns a non-success result for a command, or null to let it succeed.</summary>
        public Func<string, PactlResult?>? Fail { get; set; }

        /// <summary>Runs before a command is answered — throw here to simulate an I/O failure.</summary>
        public Action<string>? OnCall { get; set; }

        public List<string> Calls { get; } = new();

        public Task<PactlResult> RunAsync(string[] arguments, CancellationToken cancellationToken)
        {
            var joined = string.Join(" ", arguments);
            Calls.Add(joined);
            OnCall?.Invoke(joined);

            if (arguments is ["list", "sinks", "short"])
                return Task.FromResult(new PactlResult(0, Listing, string.Empty));

            return Task.FromResult(Fail?.Invoke(joined) ?? new PactlResult(0, string.Empty, string.Empty));
        }

        public List<string> SuspendCalls => Calls
            .Where(c => c.StartsWith("suspend-sink ", StringComparison.Ordinal))
            .ToList();

        public List<string> SuspendCallsFor(string sink) => Calls
            .Where(c => c.StartsWith($"suspend-sink {sink} ", StringComparison.Ordinal))
            .ToList();
    }

    private static PactlResult? FailOnly(string call, string target) =>
        call == target ? new PactlResult(1, string.Empty, "Connection failure") : null;

    private static SinkResetService Service(
        FakePactl pactl,
        ILogger<SinkResetService>? logger = null,
        bool enabled = true,
        bool mockHardware = false) =>
        new(
            logger ?? new CapturingLogger<SinkResetService>(),
            mockHardware,
            enabled,
            pactl.RunAsync,
            settleDelayMs: 0);

    [Fact]
    public void ParseSinksShort_ReadsEveryRowOfTheIssueFixture()
    {
        var rows = SinkResetService.ParseSinksShort(Issue281SinksShort);

        Assert.Equal(12, rows.Count);
        Assert.Equal(1u, rows[1].Index);
        Assert.Equal(XfiSink, rows[1].Name);
        Assert.Equal("module-alsa-card.c", rows[1].Driver);
        Assert.Equal("RUNNING", rows[1].State);
    }

    [Fact]
    public void ParseSinksShort_AcceptsSpaceSeparatedOutput()
    {
        // The issue paste arrives space-separated rather than tab-separated; the sample-spec
        // column contains spaces of its own, so the state column must still be read correctly.
        var spaced = Issue281SinksShort.Replace('\t', ' ');

        var rows = SinkResetService.ParseSinksShort(spaced);

        Assert.Equal(12, rows.Count);
        Assert.Equal("RUNNING", rows[1].State);
        Assert.Equal(XfiSink, rows[1].Name);
    }

    [Fact]
    public void IsResettable_SelectsHardwareSinksAndRejectsRemaps()
    {
        var rows = SinkResetService.ParseSinksShort(Issue281SinksShort);

        var selected = rows.Where(SinkResetService.IsResettable).ToList();

        Assert.Equal(5, selected.Count);
        Assert.All(selected, s => Assert.Equal("module-alsa-card.c", s.Driver));
        Assert.DoesNotContain(selected, s => s.Driver == "module-remap-sink.c");
        Assert.Equal(7, rows.Count(r => r.Driver == "module-remap-sink.c"));
    }

    [Fact]
    public void IsResettable_SkipsSuspendedSinks()
    {
        // A SUSPENDED sink is already closed and will open fresh on first use — resuming it would
        // force a device open nobody asked for.
        var suspended = Issue281SinksShort.Replace(
            "s32le 8ch 96000Hz\tRUNNING",
            "s32le 8ch 96000Hz\tSUSPENDED");

        var selected = SinkResetService.ParseSinksShort(suspended).Where(SinkResetService.IsResettable).ToList();

        Assert.Equal(4, selected.Count);
        Assert.DoesNotContain(selected, s => s.Name == XfiSink);
    }

    [Fact]
    public async Task ResetAllHardwareSinks_EmitsSuspendThenResumeOncePerHardwareSink()
    {
        var pactl = new FakePactl();

        var cycled = await Service(pactl).ResetAllHardwareSinksAsync();

        Assert.Equal(5, cycled);
        Assert.Equal(
            [
                "suspend-sink alsa_output.pci-0000_01_00.0.iec958-stereo 1",
                "suspend-sink alsa_output.pci-0000_01_00.0.iec958-stereo 0",
                $"suspend-sink {XfiSink} 1",
                $"suspend-sink {XfiSink} 0",
                "suspend-sink alsa_output.usb-AudioQuest_AudioQuest_DragonFly_Black_v1.5_AQDFBL0120005724-00.iec958-stereo 1",
                "suspend-sink alsa_output.usb-AudioQuest_AudioQuest_DragonFly_Black_v1.5_AQDFBL0120005724-00.iec958-stereo 0",
                "suspend-sink alsa_output.usb-Generic_ELEGIANT_SR030_20170726905959-00.analog-stereo 1",
                "suspend-sink alsa_output.usb-Generic_ELEGIANT_SR030_20170726905959-00.analog-stereo 0",
                "suspend-sink alsa_output.pci-0000_08_00.6.analog-surround-51 1",
                "suspend-sink alsa_output.pci-0000_08_00.6.analog-surround-51 0",
            ],
            pactl.SuspendCalls);
    }

    [Fact]
    public async Task ResetAllHardwareSinks_ContinuesAfterOneSinkFails()
    {
        var pactl = new FakePactl
        {
            Fail = call => call.Contains(XfiSink, StringComparison.Ordinal)
                ? new PactlResult(1, string.Empty, "Failure: No such entity")
                : null
        };

        var cycled = await Service(pactl).ResetAllHardwareSinksAsync();

        // The failing card is reported as not cycled, but the other four still are.
        Assert.Equal(4, cycled);
        Assert.Contains("suspend-sink alsa_output.pci-0000_08_00.6.analog-surround-51 0", pactl.SuspendCalls);
    }

    [Fact]
    public async Task ResumeFailure_IsRetriedBeforeGivingUp()
    {
        // The resume is the half that must not be given up on: a sink left suspended is silent,
        // and with startup as the only trigger nothing else comes back for it.
        var pactl = new FakePactl { Fail = call => FailOnly(call, $"suspend-sink {XfiSink} 0") };

        await Service(pactl).ResetAllHardwareSinksAsync();

        Assert.Equal(
            [
                $"suspend-sink {XfiSink} 1",
                $"suspend-sink {XfiSink} 0",
                $"suspend-sink {XfiSink} 0",
                $"suspend-sink {XfiSink} 0",
            ],
            pactl.SuspendCallsFor(XfiSink));
    }

    [Fact]
    public async Task ASuspendThatThrew_StillIssuesTheResume()
    {
        // pactl may have reached PulseAudio before the runner saw the failure, so the sink could
        // already be suspended — skipping the resume would leave it silent.
        var pactl = new FakePactl
        {
            OnCall = call =>
            {
                if (call == $"suspend-sink {XfiSink} 1")
                    throw new IOException("pipe closed");
            }
        };

        await Service(pactl).ResetAllHardwareSinksAsync();

        Assert.Contains($"suspend-sink {XfiSink} 0", pactl.SuspendCallsFor(XfiSink));
    }

    [Fact]
    public async Task CancellationBetweenTheHalves_StillResumesTheSink()
    {
        // Container shutdown lands here: without an uncancellable resume the card stays silent
        // across the restart.
        using var cts = new CancellationTokenSource();
        var pactl = new FakePactl
        {
            OnCall = call =>
            {
                if (call == $"suspend-sink {XfiSink} 1")
                    cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(pactl).ResetAllHardwareSinksAsync(cts.Token));

        // The cycle in flight finished its resume before the pass gave up on the remaining sinks.
        Assert.Equal(
            [$"suspend-sink {XfiSink} 1", $"suspend-sink {XfiSink} 0"],
            pactl.SuspendCallsFor(XfiSink));
    }

    [Fact]
    public async Task ASinkThatCannotBeResumed_LogsHowToRecoverItByHand()
    {
        // Startup is the only pass, so nothing recovers it automatically. The operator has to be
        // told, in terms they can act on.
        var logger = new CapturingLogger<SinkResetService>();
        var pactl = new FakePactl { Fail = call => FailOnly(call, $"suspend-sink {XfiSink} 0") };

        await Service(pactl, logger).ResetAllHardwareSinksAsync();

        var error = Assert.Single(logger.Entries.Where(e => e.Level == LogLevel.Error));
        Assert.Contains(XfiSink, error.Message, StringComparison.Ordinal);
        Assert.Contains($"pactl suspend-sink {XfiSink} 0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuspendThatFailedOutright_IsNotReportedAsStranded()
    {
        // pactl said the suspend did not happen, so the sink is still open — no alarming error.
        var logger = new CapturingLogger<SinkResetService>();
        var pactl = new FakePactl
        {
            Fail = call => call.Contains(XfiSink, StringComparison.Ordinal)
                ? new PactlResult(1, string.Empty, "Failure: No such entity")
                : null
        };

        await Service(pactl, logger).ResetAllHardwareSinksAsync();

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Disabled_EmitsNoPactlCallsAtAll()
    {
        var pactl = new FakePactl();

        Assert.Equal(0, await Service(pactl, enabled: false).ResetAllHardwareSinksAsync());
        Assert.Empty(pactl.Calls);
    }

    [Fact]
    public async Task MockHardware_EmitsNoPactlCallsAtAll()
    {
        var pactl = new FakePactl();

        Assert.Equal(0, await Service(pactl, mockHardware: true).ResetAllHardwareSinksAsync());
        Assert.Empty(pactl.Calls);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("anything", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    public void ParseResetSinks_DefaultsOnAndOnlyFalseyValuesDisable(string? value, bool expected)
    {
        Assert.Equal(expected, SinkResetService.ParseResetSinks(value));
    }
}
