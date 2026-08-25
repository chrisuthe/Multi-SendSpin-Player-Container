using MultiRoomAudio.Audio.PulseAudio;
using MultiRoomAudio.Services;
using Xunit;

namespace MultiRoomAudio.Tests.Services;

/// <summary>
/// Tests for <see cref="SinkResetService"/>, the suspend/resume workaround for cards that open on
/// the broken ALSA mmap+timer path (#281). The fixture is the <c>pactl list sinks short</c> output
/// pasted in that issue: five hardware sinks off the X-Fi host's cards, seven remap sinks on top.
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
    /// Records the pactl calls a run emits and answers the sink listing from a fixture.
    /// </summary>
    private sealed class FakePactl
    {
        private readonly string _listing;
        private readonly Func<string, PactlResult>? _suspendResult;

        public FakePactl(string listing, Func<string, PactlResult>? suspendResult = null)
        {
            _listing = listing;
            _suspendResult = suspendResult;
        }

        public List<string> Calls { get; } = new();

        public Task<PactlResult> RunAsync(string[] arguments, CancellationToken cancellationToken)
        {
            var joined = string.Join(" ", arguments);
            Calls.Add(joined);

            if (arguments is ["list", "sinks", "short"])
                return Task.FromResult(new PactlResult(0, _listing, string.Empty));

            var failure = _suspendResult?.Invoke(joined);
            return Task.FromResult(failure ?? new PactlResult(0, string.Empty, string.Empty));
        }
    }

    private static SinkResetService Service(
        FakePactl pactl,
        bool enabled = true,
        bool mockHardware = false,
        Func<DateTimeOffset>? now = null) =>
        new(
            new CapturingLogger<SinkResetService>(),
            mockHardware,
            enabled,
            pactl.RunAsync,
            settleDelayMs: 0,
            now ?? (() => DateTimeOffset.UnixEpoch));

    private static List<string> SuspendCalls(FakePactl pactl) =>
        pactl.Calls.Where(c => c.StartsWith("suspend-sink ", StringComparison.Ordinal)).ToList();

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
        var pactl = new FakePactl(Issue281SinksShort);

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
            SuspendCalls(pactl));
    }

    [Fact]
    public async Task ResetAllHardwareSinks_ContinuesAfterOneSinkFails()
    {
        var pactl = new FakePactl(
            Issue281SinksShort,
            call => call.Contains(XfiSink, StringComparison.Ordinal)
                ? new PactlResult(1, string.Empty, "Failure: No such entity")
                : new PactlResult(0, string.Empty, string.Empty));

        var cycled = await Service(pactl).ResetAllHardwareSinksAsync();

        // The failing card is reported as not cycled, but the other four still are.
        Assert.Equal(4, cycled);
        Assert.Contains("suspend-sink alsa_output.pci-0000_08_00.6.analog-surround-51 0", SuspendCalls(pactl));
    }

    [Fact]
    public async Task ResetSinkByIndex_CyclesOnlyThatSink()
    {
        var pactl = new FakePactl(Issue281SinksShort);

        var cycled = await Service(pactl).ResetSinkByIndexAsync(1);

        Assert.Equal(1, cycled);
        Assert.Equal(
            [$"suspend-sink {XfiSink} 1", $"suspend-sink {XfiSink} 0"],
            SuspendCalls(pactl));
    }

    [Fact]
    public async Task ResetSinkByIndex_IgnoresRemapSinks()
    {
        var pactl = new FakePactl(Issue281SinksShort);

        var cycled = await Service(pactl).ResetSinkByIndexAsync(5);

        Assert.Equal(0, cycled);
        Assert.Empty(SuspendCalls(pactl));
    }

    [Fact]
    public async Task ResetSinkByIndex_CooldownCollapsesAnEventBurst()
    {
        // A PulseAudio restart re-announces every sink at once; the sink must be cycled once.
        var clock = DateTimeOffset.UnixEpoch;
        var pactl = new FakePactl(Issue281SinksShort);
        var service = Service(pactl, now: () => clock);

        Assert.Equal(1, await service.ResetSinkByIndexAsync(1));
        clock += TimeSpan.FromSeconds(5);
        Assert.Equal(0, await service.ResetSinkByIndexAsync(1));

        Assert.Equal(2, SuspendCalls(pactl).Count);

        // Past the cooldown a genuine re-open is cycled again.
        clock += TimeSpan.FromSeconds(31);
        Assert.Equal(1, await service.ResetSinkByIndexAsync(1));
        Assert.Equal(4, SuspendCalls(pactl).Count);
    }

    [Fact]
    public async Task ResetCardSinks_CyclesOnlyThatCardsSinks()
    {
        var pactl = new FakePactl(Issue281SinksShort);

        var cycled = await Service(pactl).ResetCardSinksAsync("alsa_card.pci-0000_04_00.0");

        Assert.Equal(1, cycled);
        Assert.Equal(
            [$"suspend-sink {XfiSink} 1", $"suspend-sink {XfiSink} 0"],
            SuspendCalls(pactl));
    }

    [Fact]
    public async Task ResetCardSinks_IsNotSubjectToTheEventCooldown()
    {
        // A profile change recreates the sink, so it must be cycled even right after a startup pass.
        var pactl = new FakePactl(Issue281SinksShort);
        var service = Service(pactl);

        await service.ResetAllHardwareSinksAsync();
        var cycled = await service.ResetCardSinksAsync("alsa_card.pci-0000_04_00.0");

        Assert.Equal(1, cycled);
    }

    [Fact]
    public async Task Disabled_EmitsNoPactlCallsAtAll()
    {
        var pactl = new FakePactl(Issue281SinksShort);
        var service = Service(pactl, enabled: false);

        Assert.Equal(0, await service.ResetAllHardwareSinksAsync());
        Assert.Equal(0, await service.ResetSinkByIndexAsync(1));
        Assert.Equal(0, await service.ResetCardSinksAsync("alsa_card.pci-0000_04_00.0"));

        Assert.Empty(pactl.Calls);
    }

    [Fact]
    public async Task MockHardware_EmitsNoPactlCallsAtAll()
    {
        var pactl = new FakePactl(Issue281SinksShort);
        var service = Service(pactl, mockHardware: true);

        Assert.Equal(0, await service.ResetAllHardwareSinksAsync());
        Assert.Equal(0, await service.ResetSinkByIndexAsync(1));
        Assert.Equal(0, await service.ResetCardSinksAsync("alsa_card.pci-0000_04_00.0"));

        Assert.Empty(pactl.Calls);
    }

    /// <summary>
    /// Fake with a swappable listing and failure rule, for tests that need a second pass to see a
    /// different world than the first.
    /// </summary>
    private sealed class ScriptedPactl
    {
        private readonly object _lock = new();

        public string Listing { get; set; } = Issue281SinksShort;

        public Func<string, PactlResult>? Fail { get; set; }

        public Func<string, Task>? OnCall { get; set; }

        public List<string> Calls { get; } = new();

        public async Task<PactlResult> RunAsync(string[] arguments, CancellationToken cancellationToken)
        {
            // Yield so concurrent callers actually interleave rather than running to completion inline.
            await Task.Yield();

            var joined = string.Join(" ", arguments);
            lock (_lock)
            {
                Calls.Add(joined);
            }

            if (OnCall is not null)
                await OnCall(joined);

            if (arguments is ["list", "sinks", "short"])
                return new PactlResult(0, Listing, string.Empty);

            return Fail?.Invoke(joined) ?? new PactlResult(0, string.Empty, string.Empty);
        }

        public List<string> SuspendCallsFor(string sink)
        {
            lock (_lock)
            {
                return Calls
                    .Where(c => c.StartsWith($"suspend-sink {sink} ", StringComparison.Ordinal))
                    .ToList();
            }
        }
    }

    private static SinkResetService Service(ScriptedPactl pactl, int settleDelayMs = 0) =>
        new(
            new CapturingLogger<SinkResetService>(),
            mockHardware: false,
            enabled: true,
            pactl.RunAsync,
            settleDelayMs,
            () => DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ResumeFailure_IsRetriedRatherThanLeavingTheSinkSuspended()
    {
        var pactl = new ScriptedPactl
        {
            Fail = call => call == $"suspend-sink {XfiSink} 0"
                ? new PactlResult(1, string.Empty, "Connection failure")
                : new PactlResult(0, string.Empty, string.Empty)
        };

        await Service(pactl).ResetSinkByIndexAsync(1);

        // One suspend, then every resume attempt — the half that must not be given up on.
        Assert.Equal(
            [$"suspend-sink {XfiSink} 1", $"suspend-sink {XfiSink} 0", $"suspend-sink {XfiSink} 0", $"suspend-sink {XfiSink} 0"],
            pactl.SuspendCallsFor(XfiSink));
    }

    [Fact]
    public async Task ASinkStrandedBySuspend_IsRecoveredOnTheNextPass()
    {
        // Without this, a single transient resume failure would silence the card for the life of
        // the container: it is left SUSPENDED, and IsResettable skips every SUSPENDED sink.
        var pactl = new ScriptedPactl
        {
            Fail = call => call == $"suspend-sink {XfiSink} 0"
                ? new PactlResult(1, string.Empty, "Connection failure")
                : new PactlResult(0, string.Empty, string.Empty)
        };
        var service = Service(pactl);

        await service.ResetSinkByIndexAsync(1);

        // PulseAudio now reports the card as suspended, and the transient failure has cleared.
        pactl.Listing = Issue281SinksShort.Replace("s32le 8ch 96000Hz\tRUNNING", "s32le 8ch 96000Hz\tSUSPENDED");
        pactl.Fail = null;
        pactl.Calls.Clear();

        await service.ResetAllHardwareSinksAsync();

        // Recovered by a bare resume — never re-suspended, since it is already closed.
        Assert.Equal([$"suspend-sink {XfiSink} 0"], pactl.SuspendCallsFor(XfiSink));
    }

    [Fact]
    public async Task RecoveredSink_IsNotChasedForever()
    {
        var pactl = new ScriptedPactl
        {
            Fail = call => call == $"suspend-sink {XfiSink} 0"
                ? new PactlResult(1, string.Empty, "Connection failure")
                : new PactlResult(0, string.Empty, string.Empty)
        };
        var service = Service(pactl);

        await service.ResetSinkByIndexAsync(1);

        // Something else brought it back — a manual pactl, or a PulseAudio restart.
        pactl.Fail = null;
        await service.ResetAllHardwareSinksAsync();
        pactl.Calls.Clear();

        await service.ResetAllHardwareSinksAsync();

        // A normal cycle, with no lingering recovery resume in front of it.
        Assert.Equal(
            [$"suspend-sink {XfiSink} 1", $"suspend-sink {XfiSink} 0"],
            pactl.SuspendCallsFor(XfiSink));
    }

    [Fact]
    public async Task CancellationBetweenTheHalves_StillResumesTheSink()
    {
        // Container shutdown lands here: without an uncancellable resume the card stays silent
        // across the restart.
        using var cts = new CancellationTokenSource();
        var pactl = new ScriptedPactl();
        pactl.OnCall = call =>
        {
            if (call == $"suspend-sink {XfiSink} 1")
                cts.Cancel();
            return Task.CompletedTask;
        };

        await Service(pactl, settleDelayMs: 5).ResetSinkByIndexAsync(1, cts.Token);

        Assert.Equal(
            [$"suspend-sink {XfiSink} 1", $"suspend-sink {XfiSink} 0"],
            pactl.SuspendCallsFor(XfiSink));
    }

    [Fact]
    public async Task OverlappingResets_CannotEnterTheSameSinkAtOnce()
    {
        // A profile change and the sink-appeared event it causes can both target the same sink.
        // If they interleave, one cycle's resume lands right after the other's suspend and the
        // device never closes — the cycle silently stops doing the one thing it exists to do.
        // Hold the first cycle inside its suspend, then prove a second cannot start one.
        var firstSuspendSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pactl = new ScriptedPactl();
        pactl.OnCall = async call =>
        {
            if (call != $"suspend-sink {XfiSink} 1")
                return;

            firstSuspendSeen.TrySetResult();
            await release.Task;
        };

        var service = Service(pactl);

        var first = service.ResetCardSinksAsync("alsa_card.pci-0000_04_00.0");
        await firstSuspendSeen.Task;

        // The first cycle now owns the sink, parked between its suspend and its resume.
        var second = service.ResetAllHardwareSinksAsync();
        await Task.Delay(100);

        Assert.Single(pactl.SuspendCallsFor(XfiSink));

        release.TrySetResult();
        await Task.WhenAll(first, second);

        // Both cycles ran, one after the other, each a complete suspend/resume pair.
        Assert.Equal(
            [
                $"suspend-sink {XfiSink} 1",
                $"suspend-sink {XfiSink} 0",
                $"suspend-sink {XfiSink} 1",
                $"suspend-sink {XfiSink} 0",
            ],
            pactl.SuspendCallsFor(XfiSink));
    }

    /// <summary>
    /// Clock that parks the first callers until <paramref name="gate"/> of them have arrived, so a
    /// test can hold two racing cooldown checks inside the window at once. Falls back to a timeout
    /// so it cannot hang when the code under test correctly serializes those callers.
    /// </summary>
    private sealed class RendezvousClock
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _gate;
        private int _arrivals;

        public RendezvousClock(int gate) => _gate = gate;

        public DateTimeOffset Read()
        {
            if (Interlocked.Increment(ref _arrivals) >= _gate)
                _arrived.TrySetResult();
            else
                _arrived.Task.Wait(TimeSpan.FromMilliseconds(200));

            return DateTimeOffset.UnixEpoch;
        }
    }

    [Fact]
    public async Task ConcurrentSinkEvents_StillCollapseToOneCycle()
    {
        // The cooldown is a check-then-act on _lastCycled. Held open, two events for the same sink
        // both read it as clear, and the sink gets interrupted twice — so the check has to happen
        // under the same gate as the write.
        var clock = new RendezvousClock(gate: 2);
        var pactl = new ScriptedPactl();
        var service = new SinkResetService(
            new CapturingLogger<SinkResetService>(),
            mockHardware: false,
            enabled: true,
            pactl.RunAsync,
            settleDelayMs: 0,
            clock.Read);

        var cycles = await Task.WhenAll(
            service.ResetSinkByIndexAsync(1),
            service.ResetSinkByIndexAsync(1));

        Assert.Equal(1, cycles.Sum());
        Assert.Equal(
            [$"suspend-sink {XfiSink} 1", $"suspend-sink {XfiSink} 0"],
            pactl.SuspendCallsFor(XfiSink));
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
