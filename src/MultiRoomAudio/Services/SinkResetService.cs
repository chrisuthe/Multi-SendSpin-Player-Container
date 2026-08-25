using System.Text.RegularExpressions;
using MultiRoomAudio.Audio.PulseAudio;

namespace MultiRoomAudio.Services;

/// <summary>
/// Suspends and immediately resumes hardware sinks at startup, so cards that opened on the broken
/// ALSA mmap+timer transfer path start moving audio again (#281).
/// </summary>
/// <remarks>
/// The stall is a kernel regression (391e69143d0a, fixed upstream by e9418da50d9e): the sink
/// accepts samples and reports RUNNING while nothing reaches the DAC. A suspend/resume forces
/// PulseAudio to re-open the device and re-negotiate the transfer mode.
/// <para>
/// Startup is deliberately the only trigger. HAOS's PulseAudio loads no module-suspend-on-idle, so
/// a sink stays open once opened and one cycle per open is enough. Cycling on sink-appeared events
/// and card profile changes as well was tried and dropped: those overlap each other and the startup
/// pass, which needed a per-sink cooldown, a per-sink mutex and cross-pass recovery state to keep
/// straight — a lot of machinery guarding against pactl failures that only arose because of the
/// extra suspends. The trade is that a PulseAudio restart or a profile change which re-opens a
/// device on the bad path needs an add-on restart to clear.
/// </para>
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

    /// <summary>Attempts made to resume a sink before giving up and telling the operator.</summary>
    private const int ResumeAttempts = 3;

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

    public SinkResetService(ILogger<SinkResetService> logger, EnvironmentService environment)
        : this(
            logger,
            environment.IsMockHardware,
            ParseResetSinks(Environment.GetEnvironmentVariable(ResetSinksEnv)),
            (args, ct) => PactlCommandRunner.RunAsync(args, ct),
            DefaultSettleDelayMs)
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
        int settleDelayMs)
    {
        _logger = logger;
        _mockHardware = mockHardware;
        _enabled = enabled;
        _runner = runner;
        _settleDelayMs = settleDelayMs;
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
    /// Cycles every open hardware sink. Called once from the <c>sinkreset</c> startup phase, after
    /// the custom sinks are attached (so masters are genuinely open) and before players start.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many sinks completed a full suspend/resume.</returns>
    public async Task<int> ResetAllHardwareSinksAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Sink reset skipped: {Env}=false", ResetSinksEnv);
            return 0;
        }

        if (_mockHardware)
        {
            _logger.LogDebug("Sink reset skipped: mock hardware mode");
            return 0;
        }

        var listing = await _runner(["list", "sinks", "short"], cancellationToken);
        if (!listing.Success)
        {
            _logger.LogWarning("Sink reset skipped: could not list sinks: {Error}", listing.Error);
            return 0;
        }

        var all = ParseSinksShort(listing.Output);
        var candidates = all.Where(IsResettable).ToList();

        if (candidates.Count == 0)
        {
            // A PipeWire host reports its own driver name rather than module-alsa-card.c, so nothing
            // is ever selected and the workaround is inert. Worth saying out loud.
            _logger.LogInformation(
                "No open {Driver} sinks among {Total} sink(s) — nothing to suspend/resume",
                HardwareDriver, all.Count);
            return 0;
        }

        var cycled = 0;
        foreach (var sink in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await CycleAsync(sink, cancellationToken))
                cycled++;

            // CycleAsync swallows cancellation so its resume always runs. Surface it here, once the
            // sink is safely back, or a pass interrupted on its last sink would return normally and
            // the startup phase would be reported complete during a shutdown.
            cancellationToken.ThrowIfCancellationRequested();
        }

        _logger.LogInformation("Suspend/resume cycled {Count} of {Total} hardware sink(s)",
            cycled, candidates.Count);

        return cycled;
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

    /// <summary>
    /// Runs the suspend/resume pair for one sink. Swallows every failure so a card that refuses
    /// the cycle does not stop the remaining sinks from being cycled.
    /// </summary>
    private async Task<bool> CycleAsync(PactlSinkRow sink, CancellationToken cancellationToken)
    {
        if (!PactlCommandRunner.ValidateName(sink.Name, out var nameError))
        {
            _logger.LogWarning("Refusing to cycle sink '{Sink}': {Error}", sink.Name, nameError);
            return false;
        }

        _logger.LogInformation("Cycling sink '{Sink}' (state {State})", sink.Name, sink.State);

        // Two separate questions: did the suspend definitely land (so this counts as a cycle),
        // and could it possibly have landed (so the sink might now be silent)?
        var suspendConfirmed = false;
        var mayBeSuspended = false;
        try
        {
            var suspend = await _runner(["suspend-sink", sink.Name, "1"], cancellationToken);
            suspendConfirmed = suspend.Success;
            mayBeSuspended = suspend.Success;

            if (!suspend.Success)
            {
                _logger.LogWarning("Suspending sink '{Sink}' failed: {Error}", sink.Name, suspend.Error);
            }

            await Task.Delay(_settleDelayMs, cancellationToken);
        }
        catch (Exception ex)
        {
            // Includes cancellation: shutdown must not skip the resume below. pactl may already
            // have reached PulseAudio before this threw, so assume the suspend might have landed.
            mayBeSuspended = true;
            _logger.LogWarning(ex, "Suspending sink '{Sink}' failed", sink.Name);
        }

        // Always resume, and never on the caller's token — a sink left suspended is silent until
        // something resumes it, and nothing else will.
        var resumed = await ResumeAsync(sink.Name);
        if (resumed)
        {
            _logger.LogInformation("Sink '{Sink}' suspended and resumed", sink.Name);
        }
        else if (mayBeSuspended)
        {
            _logger.LogError(
                "Sink '{Sink}' may be left suspended and silent — to recover it, " +
                "run: pactl suspend-sink {SinkName} 0",
                sink.Name, sink.Name);
        }

        return suspendConfirmed && resumed;
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
}

/// <summary>
/// One row of <c>pactl list sinks short</c>.
/// </summary>
/// <param name="Index">PulseAudio sink index.</param>
/// <param name="Name">Sink name.</param>
/// <param name="Driver">Owning module, e.g. <c>module-alsa-card.c</c>.</param>
/// <param name="State">Sink state, e.g. <c>RUNNING</c>, <c>IDLE</c>, <c>SUSPENDED</c>.</param>
internal record PactlSinkRow(uint Index, string Name, string Driver, string State);
