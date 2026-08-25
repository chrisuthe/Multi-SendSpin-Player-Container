using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MultiRoomAudio.Audio.PulseAudio;

namespace MultiRoomAudio.Services;

/// <summary>
/// Suspends and immediately resumes hardware sinks so cards that opened on the broken ALSA
/// mmap+timer transfer path start moving audio again (#281).
/// </summary>
/// <remarks>
/// The stall is a kernel regression (391e69143d0a, fixed upstream by e9418da50d9e): the sink
/// accepts samples and reports RUNNING while nothing reaches the DAC. A suspend/resume forces
/// PulseAudio to re-open the device and re-negotiate the transfer mode. HAOS's PulseAudio loads
/// no module-suspend-on-idle, so once a sink is cycled it stays open on the good path — a single
/// cycle per open is enough, which is why this runs at startup and on the events that re-open a
/// device rather than per stream.
/// </remarks>
public partial class SinkResetService
{
    /// <summary>
    /// Runs a pactl command. Injected so tests can assert the emitted sequence without PulseAudio.
    /// </summary>
    internal delegate Task<PactlResult> PactlRunner(string[] arguments, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>pactl list sinks short</c> driver column that identifies a real ALSA card.
    /// Remap and combine sinks report their own module here and must never be cycled.
    /// </summary>
    private const string HardwareDriver = "module-alsa-card.c";

    /// <summary>Sink state for a device PulseAudio has already closed.</summary>
    private const string SuspendedState = "SUSPENDED";

    /// <summary>Pause between suspend and resume, giving PulseAudio time to close the device.</summary>
    private const int DefaultSettleDelayMs = 250;

    /// <summary>Attempts made to resume a sink before it is recorded as stranded.</summary>
    private const int ResumeAttempts = 3;

    /// <summary>
    /// Window during which a sink will not be cycled again by a sink-appeared event. A PulseAudio
    /// restart recreates every sink at once, so the burst collapses to one cycle per sink.
    /// </summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private const string ResetSinksEnv = "RESET_SINKS";

    /// <summary>
    /// Parses a row of <c>pactl list sinks short</c>: index, name, driver, sample spec, state.
    /// The sample spec contains spaces, so it is matched as the slack between the fixed columns;
    /// this tolerates both the tab-separated real output and space-separated pasted output.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<index>\d+)\s+(?<name>\S+)\s+(?<driver>\S+)\s+(?<spec>.*\S)\s+(?<state>\S+)\s*$",
        RegexOptions.Compiled)]
    private static partial Regex SinkShortRowPattern();

    private readonly ILogger<SinkResetService> _logger;
    private readonly bool _mockHardware;
    private readonly bool _enabled;
    private readonly PactlRunner _runner;
    private readonly int _settleDelayMs;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCycled = new(StringComparer.Ordinal);

    /// <summary>
    /// Sinks this service suspended but could not resume. A suspended sink is silent and
    /// <see cref="IsResettable"/> skips it, so without this the workaround could mute a healthy
    /// card for good after one transient pactl failure. Every later pass retries these first.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _awaitingResume = new(StringComparer.Ordinal);

    /// <summary>
    /// One gate per sink, so overlapping triggers (a profile change and the sink-appeared event it
    /// causes, or a restored card and the startup pass) cannot interleave their halves and land a
    /// resume immediately after a suspend — which would never close the device the cycle exists to reopen.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sinkGates = new(StringComparer.Ordinal);

    public SinkResetService(ILogger<SinkResetService> logger, EnvironmentService environment)
        : this(
            logger,
            environment.IsMockHardware,
            ParseResetSinks(Environment.GetEnvironmentVariable(ResetSinksEnv)),
            (args, ct) => PactlCommandRunner.RunAsync(args, ct),
            DefaultSettleDelayMs,
            () => DateTimeOffset.UtcNow)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Sink suspend/resume workaround disabled by {Env}=false", ResetSinksEnv);
        }
    }

    internal SinkResetService(
        ILogger<SinkResetService> logger,
        bool mockHardware,
        bool enabled,
        PactlRunner runner,
        int settleDelayMs,
        Func<DateTimeOffset> now)
    {
        _logger = logger;
        _mockHardware = mockHardware;
        _enabled = enabled;
        _runner = runner;
        _settleDelayMs = settleDelayMs;
        _now = now;
    }

    /// <summary>
    /// Parses <c>RESET_SINKS</c>. The workaround is on by default — the option exists only to back
    /// it out — so anything but an explicit falsey value enables it.
    /// </summary>
    internal static bool ParseResetSinks(string? value) =>
        value is null ||
        !(value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
          value == "0" ||
          value.Equals("no", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cycles every open hardware sink. Called from the <c>sinkreset</c> startup phase, after the
    /// custom sinks are attached (so masters are genuinely open) and before players start.
    /// </summary>
    public Task<int> ResetAllHardwareSinksAsync(CancellationToken cancellationToken = default) =>
        ResetAsync(_ => true, "startup", respectCooldown: false, announceEmpty: true, cancellationToken);

    /// <summary>
    /// Cycles the sink PulseAudio just announced, if it is a hardware sink. Covers a supervisor
    /// PulseAudio restart or a card hotplug re-opening devices without an add-on restart.
    /// </summary>
    public Task<int> ResetSinkByIndexAsync(uint index, CancellationToken cancellationToken = default) =>
        ResetAsync(s => s.Index == index, $"sink #{index} appeared", respectCooldown: true, announceEmpty: false, cancellationToken);

    /// <summary>
    /// Cycles the hardware sinks belonging to a card. A profile change tears down and recreates
    /// them, so they can land back on the broken open.
    /// </summary>
    public Task<int> ResetCardSinksAsync(string cardName, CancellationToken cancellationToken = default)
    {
        var identifier = ExtractCardIdentifier(cardName);
        if (string.IsNullOrEmpty(identifier))
        {
            _logger.LogDebug("Cannot derive a device identifier from card '{Card}'; skipping sink reset", cardName);
            return Task.FromResult(0);
        }

        return ResetAsync(
            s => s.Name.Contains(identifier, StringComparison.OrdinalIgnoreCase),
            $"card '{cardName}' profile change",
            respectCooldown: false,
            announceEmpty: false,
            cancellationToken);
    }

    /// <summary>
    /// Strips the card-name prefix to leave the device identifier shared with its sink names,
    /// e.g. "alsa_card.pci-0000_04_00.0" → "pci-0000_04_00.0".
    /// </summary>
    private static string ExtractCardIdentifier(string cardName)
    {
        if (string.IsNullOrWhiteSpace(cardName))
            return string.Empty;

        const string prefix = "alsa_card.";
        return cardName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? cardName[prefix.Length..]
            : cardName;
    }

    /// <summary>
    /// Parses <c>pactl list sinks short</c> output into rows, ignoring anything unparseable.
    /// </summary>
    internal static IReadOnlyList<PactlSinkRow> ParseSinksShort(string output)
    {
        var rows = new List<PactlSinkRow>();
        if (string.IsNullOrWhiteSpace(output))
            return rows;

        foreach (var line in output.Split('\n'))
        {
            var match = SinkShortRowPattern().Match(line.TrimEnd('\r'));
            if (!match.Success)
                continue;

            if (!uint.TryParse(match.Groups["index"].Value, out var index))
                continue;

            rows.Add(new PactlSinkRow(
                index,
                match.Groups["name"].Value,
                match.Groups["driver"].Value,
                match.Groups["state"].Value));
        }

        return rows;
    }

    /// <summary>
    /// Whether a sink is worth cycling: a real ALSA card that PulseAudio currently has open.
    /// A SUSPENDED sink is closed and will open fresh on first use, so resuming it would force
    /// a device open nobody asked for.
    /// </summary>
    internal static bool IsResettable(PactlSinkRow sink) =>
        string.Equals(sink.Driver, HardwareDriver, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(sink.State, SuspendedState, StringComparison.OrdinalIgnoreCase);

    private async Task<int> ResetAsync(
        Func<PactlSinkRow, bool> selector,
        string reason,
        bool respectCooldown,
        bool announceEmpty,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Sink reset ({Reason}) skipped: {Env}=false", reason, ResetSinksEnv);
            return 0;
        }

        if (_mockHardware)
        {
            _logger.LogDebug("Sink reset ({Reason}) skipped: mock hardware mode", reason);
            return 0;
        }

        var listing = await _runner(["list", "sinks", "short"], cancellationToken);
        if (!listing.Success)
        {
            _logger.LogWarning("Sink reset ({Reason}) skipped: could not list sinks: {Error}",
                reason, listing.Error);
            return 0;
        }

        var all = ParseSinksShort(listing.Output);

        // Before anything else, rescue sinks an earlier pass left suspended.
        await RecoverStrandedSinksAsync(all);

        var candidates = all.Where(IsResettable).Where(selector).ToList();

        if (candidates.Count == 0)
        {
            // Worth surfacing on the startup pass: a PipeWire host reports its own driver name
            // rather than module-alsa-card.c, so nothing is ever selected and the workaround is inert.
            if (announceEmpty && all.Count > 0)
            {
                _logger.LogInformation(
                    "No open {Driver} sinks among {Total} sink(s) — nothing to suspend/resume",
                    HardwareDriver, all.Count);
            }
            else
            {
                _logger.LogDebug("Sink reset ({Reason}): no open hardware sinks among {Total} sink(s)",
                    reason, all.Count);
            }

            return 0;
        }

        var cycled = 0;
        foreach (var sink in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await CycleAsync(sink, reason, respectCooldown, cancellationToken))
                cycled++;
        }

        if (cycled > 0)
        {
            _logger.LogInformation("Suspend/resume cycled {Count} hardware sink(s) ({Reason})", cycled, reason);
        }

        return cycled;
    }

    private bool InCooldown(string sinkName) =>
        _lastCycled.TryGetValue(sinkName, out var last) && _now() - last < Cooldown;

    /// <summary>
    /// Runs the suspend/resume pair for one sink. Swallows every failure so a card that refuses
    /// the cycle does not stop the remaining sinks from being cycled.
    /// </summary>
    private async Task<bool> CycleAsync(
        PactlSinkRow sink,
        string reason,
        bool respectCooldown,
        CancellationToken cancellationToken)
    {
        if (!PactlCommandRunner.ValidateName(sink.Name, out var nameError))
        {
            _logger.LogWarning("Sink reset ({Reason}): refusing to cycle '{Sink}': {Error}",
                reason, sink.Name, nameError);
            return false;
        }

        // Serialize per sink: a concurrent cycle could otherwise resume this one mid-settle.
        var gate = _sinkGates.GetOrAdd(sink.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            // Tested under the gate, not before it: a burst of events for one sink would otherwise
            // all read _lastCycled before any of them wrote it, and then cycle in turn.
            if (respectCooldown && InCooldown(sink.Name))
            {
                _logger.LogDebug("Sink reset ({Reason}): '{Sink}' cycled recently, skipping", reason, sink.Name);
                return false;
            }

            // Stamp before the cycle so a failure still absorbs the rest of an event burst.
            _lastCycled[sink.Name] = _now();

            _logger.LogInformation("Cycling sink '{Sink}' (state {State}, {Reason})", sink.Name, sink.State, reason);

            var suspended = false;
            try
            {
                var suspend = await _runner(["suspend-sink", sink.Name, "1"], cancellationToken);
                suspended = suspend.Success;

                if (!suspended)
                {
                    _logger.LogWarning("Suspending sink '{Sink}' failed: {Error}", sink.Name, suspend.Error);
                }

                await Task.Delay(_settleDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                // Includes cancellation: shutdown must not skip the resume below.
                _logger.LogWarning(ex, "Suspending sink '{Sink}' failed", sink.Name);
            }

            // Always resume, and never on the caller's token — a sink left suspended is silent, and
            // IsResettable would skip it on every later pass.
            var resumed = await ResumeAsync(sink.Name);
            if (resumed)
            {
                _awaitingResume.TryRemove(sink.Name, out _);
                _logger.LogInformation("Sink '{Sink}' suspended and resumed", sink.Name);
            }
            else if (suspended)
            {
                _awaitingResume[sink.Name] = 0;
                _logger.LogError(
                    "Sink '{Sink}' is left suspended and will be silent — the next reset pass retries it; " +
                    "to recover it now, run: pactl suspend-sink {SinkName} 0",
                    sink.Name, sink.Name);
            }

            return suspended && resumed;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Resumes a sink, retrying a few times. Deliberately uncancellable: this is the half of the
    /// cycle that must not be skipped.
    /// </summary>
    private async Task<bool> ResumeAsync(string sinkName)
    {
        for (var attempt = 1; attempt <= ResumeAttempts; attempt++)
        {
            try
            {
                var resume = await _runner(["suspend-sink", sinkName, "0"], CancellationToken.None);
                if (resume.Success)
                    return true;

                _logger.LogWarning("Resuming sink '{Sink}' failed (attempt {Attempt}/{Max}): {Error}",
                    sinkName, attempt, ResumeAttempts, resume.Error);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resuming sink '{Sink}' failed (attempt {Attempt}/{Max})",
                    sinkName, attempt, ResumeAttempts);
            }

            if (attempt < ResumeAttempts)
                await Task.Delay(_settleDelayMs, CancellationToken.None);
        }

        return false;
    }

    /// <summary>
    /// Retries the resume for any sink an earlier pass suspended but failed to bring back, so a
    /// transient pactl failure costs one pass of silence rather than the life of the container.
    /// The marker is only cleared once a resume has actually succeeded.
    /// </summary>
    private async Task RecoverStrandedSinksAsync(IReadOnlyList<PactlSinkRow> sinks)
    {
        if (_awaitingResume.IsEmpty)
            return;

        foreach (var sink in sinks)
        {
            if (!_awaitingResume.ContainsKey(sink.Name))
                continue;

            // Resume before clearing the marker, without consulting the listing's state. That row
            // was read before the gate was taken, so a concurrent cycle may have suspended the sink
            // since — and trusting a stale RUNNING would drop the marker on a sink that is actually
            // silent, with nothing left to recover it. Resuming an unsuspended sink is a no-op, so
            // the redundant call is far cheaper than the state check is risky.
            _logger.LogWarning("Sink '{Sink}' was left suspended by an earlier reset — resuming it", sink.Name);

            var gate = _sinkGates.GetOrAdd(sink.Name, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                if (await ResumeAsync(sink.Name))
                {
                    _awaitingResume.TryRemove(sink.Name, out _);
                    _logger.LogInformation("Sink '{Sink}' recovered from a stranded suspend", sink.Name);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

/// <summary>
/// One row of <c>pactl list sinks short</c>.
/// </summary>
/// <param name="Index">PulseAudio sink index.</param>
/// <param name="Name">Sink name.</param>
/// <param name="Driver">Owning module, e.g. <c>module-alsa-card.c</c>.</param>
/// <param name="State">Sink state, e.g. <c>RUNNING</c>, <c>IDLE</c>, <c>SUSPENDED</c>.</param>
internal record PactlSinkRow(uint Index, string Name, string Driver, string State);
