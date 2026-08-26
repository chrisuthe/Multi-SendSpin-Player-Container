using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/>.
/// Provides current local time to the buffer for timed sample release and surfaces
/// read/overrun diagnostics for Stats for Nerds.
/// </summary>
/// <remarks>
/// <para><strong>Overview</strong></para>
/// <para>
/// This class serves as the bridge between the Sendspin SDK's timed audio buffer and the
/// audio output system (PulseAudio or ALSA). It is called from the audio output thread's
/// write callback whenever audio samples are needed.
/// </para>
///
/// <para><strong>Thread Safety Contract</strong></para>
/// <para>
/// This class is designed to be called from a single audio thread. The following guarantees apply:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="Read"/> is called exclusively from the audio output thread (PulseAudio write callback)
///     and must complete quickly without blocking to avoid audio glitches.
///   </description></item>
///   <item><description>
///     <see cref="Reset"/> may be called from any thread to reset diagnostic state. It modifies
///     fields that are only read (not written) by the audio thread during <see cref="Read"/>,
///     so no lock is required - the audio thread will see the reset values on the next callback.
///   </description></item>
///   <item><description>
///     The diagnostic properties (<see cref="TotalReads"/>, <see cref="ZeroReads"/>, etc.) may be
///     read from any thread. They are simple scalar reads which are atomic on modern architectures.
///   </description></item>
///   <item><description>
///     The underlying <see cref="ITimedAudioBuffer"/> is thread-safe and handles its own synchronization.
///   </description></item>
/// </list>
///
/// <para><strong>Sync Correction</strong></para>
/// <para>
/// Sync correction belongs to the SDK. <see cref="ITimedAudioBuffer.Read"/> measures the error
/// between where playback should be (from the server timestamps) and where it actually is, then
/// closes it itself: frame drop/insert with 3-point weighted interpolation inside the spec's
/// +/-0.5% speed cap, escalating to a one-shot snap above ~5ms and a re-anchor above 500ms.
/// </para>
/// <para>
/// This class previously ran its own corrector against <c>ReadRaw</c>, which duplicated the SDK's
/// interpolation, exceeded the spec's speed cap by ~20x, and could not coordinate with the SDK's
/// one-shot snap tier (added in SDK 9.3.0) - both correctors would act on the same error. Reading
/// through <see cref="ITimedAudioBuffer.Read"/> puts the whole correction loop in one place, where
/// the tiers already stand down for one another. Tuning lives in
/// <c>PlayerManagerService.PulseAudioSyncOptions</c>.
/// </para>
///
/// <para><strong>Performance Considerations</strong></para>
/// <para>
/// The <see cref="Read"/> method is called from a real-time audio thread. To avoid glitches:
/// </para>
/// <list type="bullet">
///   <item><description>Reads directly into the caller's buffer - no temporary allocation</description></item>
///   <item><description>No locks or blocking operations</description></item>
///   <item><description>Diagnostic logging is rate-limited to once per second</description></item>
/// </list>
/// </remarks>
public sealed class BufferedAudioSampleSource : IAudioSampleSource
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly Func<long> _getCurrentTimeMicroseconds;
    private readonly ILogger<BufferedAudioSampleSource>? _logger;
    private readonly int _channels;
    private readonly int _sampleRate;

    // Debug logging rate limiter
    private long _lastDebugLogTime;
    private const long DebugLogIntervalMicroseconds = 1_000_000; // 1 second

    // Diagnostic counters for tracking buffer behavior
    private long _totalReads;
    private long _zeroReads;
    private long _successfulReads;
    private long _firstReadTime;
    private long _lastSuccessfulReadTime;
    private bool _hasEverReceivedSamples;

    // Overrun tracking - detect when SDK starts dropping samples
    private long _lastKnownDroppedSamples;
    private long _lastKnownOverrunCount;
    private bool _hasLoggedOverrunStart;
    private bool _hasLoggedStartupDiscard;

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the underlying timed audio buffer.
    /// </summary>
    public ITimedAudioBuffer Buffer => _buffer;

    // Diagnostic properties for Stats for Nerds
    /// <summary>Total number of read attempts.</summary>
    public long TotalReads => _totalReads;
    /// <summary>Number of reads that returned 0 samples.</summary>
    public long ZeroReads => _zeroReads;
    /// <summary>Number of reads that returned samples.</summary>
    public long SuccessfulReads => _successfulReads;
    /// <summary>Time of first read attempt in microseconds.</summary>
    public long FirstReadTime => _firstReadTime;
    /// <summary>Time of last successful read in microseconds.</summary>
    public long LastSuccessfulReadTime => _lastSuccessfulReadTime;
    /// <summary>Whether any samples have ever been received.</summary>
    public bool HasEverReceivedSamples => _hasEverReceivedSamples;
    /// <summary>Function to get current time in microseconds.</summary>
    public long CurrentTimeMicroseconds => _getCurrentTimeMicroseconds();

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferedAudioSampleSource"/> class.
    /// </summary>
    /// <param name="buffer">The timed audio buffer to read from.</param>
    /// <param name="getCurrentTimeMicroseconds">Function that returns current local time in microseconds.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public BufferedAudioSampleSource(
        ITimedAudioBuffer buffer,
        Func<long> getCurrentTimeMicroseconds,
        ILogger<BufferedAudioSampleSource>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(getCurrentTimeMicroseconds);

        _buffer = buffer;
        _getCurrentTimeMicroseconds = getCurrentTimeMicroseconds;
        _logger = logger;
        _channels = buffer.Format.Channels;
        _sampleRate = buffer.Format.SampleRate;

        if (_channels <= 0)
        {
            throw new ArgumentException("Audio format must have at least one channel.", nameof(buffer));
        }

        _logger?.LogInformation(
            "BufferedAudioSampleSource initialized: channels={Channels}, sampleRate={SampleRate}, " +
            "sync correction=SDK (ITimedAudioBuffer.Read)",
            _channels, _sampleRate);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads straight into the caller's buffer: <see cref="ITimedAudioBuffer.Read"/> applies sync
    /// correction in place, so no temporary buffer is needed on the real-time audio thread.
    /// </remarks>
    public int Read(float[] buffer, int offset, int count)
    {
        var currentTime = _getCurrentTimeMicroseconds();
        _totalReads++;

        // Track first read time for diagnostics
        if (_firstReadTime == 0)
        {
            _firstReadTime = currentTime;
        }

        // Read sync-corrected samples from the timed buffer.
        // CS0618: ITimedAudioBuffer.Read still carries an [Obsolete] attribute pointing at ReadRaw +
        // an external ISyncCorrectionProvider. That attribute is stale on the 9.x line, which froze
        // its published surface and so could not remove it: SDK 9.3.0 rewrote this method's own docs
        // to describe it as the complete path ("corrects end to end and needs nothing from the
        // caller") and put the new one-shot snap tier behind it. External correction is what we just
        // removed - it duplicated the SDK's interpolation and could not coordinate with that tier.
#pragma warning disable CS0618
        var read = _buffer.Read(buffer.AsSpan(offset, count), currentTime);
#pragma warning restore CS0618

        if (read > 0)
        {
            _successfulReads++;
            _lastSuccessfulReadTime = currentTime;

            // Log first successful read - important milestone
            if (!_hasEverReceivedSamples)
            {
                _hasEverReceivedSamples = true;
                var elapsedMs = (currentTime - _firstReadTime) / 1000.0;
                _logger?.LogInformation(
                    "First samples received from buffer: elapsedMs={ElapsedMs:F1}, " +
                    "totalReads={TotalReads}, zeroReads={ZeroReads}",
                    elapsedMs, _totalReads, _zeroReads);
            }
        }
        else
        {
            _zeroReads++;
            LogZeroRead(currentTime);
        }

        // Fill any shortfall with silence
        if (read < count)
        {
            buffer.AsSpan(offset + read, count - read).Fill(0f);
        }

        // Check for overruns (SDK dropping samples due to buffer full)
        CheckForOverruns();

        // Always return requested count to keep audio output happy
        return count;
    }

    /// <summary>
    /// Logs diagnostic information when Read returns 0 samples.
    /// </summary>
    private void LogZeroRead(long currentTime)
    {
        if (_logger == null || currentTime - _lastDebugLogTime < DebugLogIntervalMicroseconds)
        {
            return;
        }

        _lastDebugLogTime = currentTime;
        var stats = _buffer.GetStats();
        var elapsedSinceFirstMs = (currentTime - _firstReadTime) / 1000.0;
        var elapsedSinceLastSuccessMs = _lastSuccessfulReadTime > 0
            ? (currentTime - _lastSuccessfulReadTime) / 1000.0
            : -1;

        // Determine the likely reason for zero read
        string reason;
        if (!stats.IsPlaybackActive && stats.BufferedMs > 0)
        {
            reason = "SDK scheduled start not reached";
        }
        else if (stats.BufferedMs == 0)
        {
            reason = "Buffer empty";
        }
        else
        {
            reason = "Unknown";
        }

        _logger.LogWarning(
            "Read returned 0 [{Reason}]: currentTime={CurrentTime}μs, bufferedMs={BufferedMs:F0}, " +
            "targetMs={TargetMs:F0}, isPlaybackActive={IsPlaybackActive}, syncError={SyncError:F1}ms, " +
            "elapsedMs={ElapsedMs:F0}, sinceLastSuccessMs={SinceLastSuccess:F0}, " +
            "zeroReads={ZeroReads}/{TotalReads}, overruns={Overruns}, underruns={Underruns}",
            reason,
            currentTime,
            stats.BufferedMs,
            stats.TargetMs,
            stats.IsPlaybackActive,
            stats.SyncErrorMicroseconds / 1000.0,
            elapsedSinceFirstMs,
            elapsedSinceLastSuccessMs,
            _zeroReads, _totalReads,
            stats.OverrunCount,
            stats.UnderrunCount);

        _logger.LogWarning(
            "Buffer state: samplesWritten={Written}, samplesRead={Read}, " +
            "droppedOverflow={DroppedOverflow}, droppedSync={DroppedSync}, insertedSync={InsertedSync}",
            stats.TotalSamplesWritten,
            stats.TotalSamplesRead,
            stats.DroppedSamples,
            stats.SamplesDroppedForSync,
            stats.SamplesInsertedForSync);
    }

    /// <summary>
    /// Checks if the SDK has started dropping samples due to buffer overflow.
    /// </summary>
    private void CheckForOverruns()
    {
        if (_logger == null)
            return;

        var stats = _buffer.GetStats();
        var currentDropped = stats.DroppedSamples;
        var currentOverruns = stats.OverrunCount;

        var newDrops = currentDropped - _lastKnownDroppedSamples;
        var newOverruns = currentOverruns - _lastKnownOverrunCount;

        if (newDrops <= 0 && newOverruns <= 0)
        {
            return;
        }

        // A genuine overflow is signalled by the SDK's OverrunCount advancing: the buffer filled and
        // Read() failed to consume in time. A rise in DroppedSamples *without* an overrun increment is a
        // benign one-time startup discard - the SDK re-anchors playback and drops samples whose scheduled
        // play-time already passed (see issue #233). At startup the buffer is nowhere near capacity and
        // Read() is consuming normally, so reporting that as a buffer-full overrun is a false alarm.
        if (newOverruns > 0 || _hasLoggedOverrunStart)
        {
            if (!_hasLoggedOverrunStart)
            {
                _hasLoggedOverrunStart = true;
                _logger.LogError(
                    "BUFFER OVERFLOW DETECTED: SDK is dropping samples because buffer is full and Read() isn't consuming. " +
                    "bufferedMs={BufferedMs:F0}, targetMs={TargetMs:F0}, isPlaybackActive={IsPlaybackActive}, " +
                    "totalDropped={Dropped}, overrunCount={Overruns}. " +
                    "This indicates scheduled start time was never reached.",
                    stats.BufferedMs,
                    stats.TargetMs,
                    stats.IsPlaybackActive,
                    currentDropped,
                    currentOverruns);
            }
            else if (newDrops > 10000 || newOverruns > 0)
            {
                _logger.LogWarning(
                    "Buffer overflow continues: +{NewDrops} samples dropped, total={Dropped}, overruns={Overruns}, " +
                    "bufferedMs={BufferedMs:F0}, isPlaybackActive={IsPlaybackActive}",
                    newDrops, currentDropped, currentOverruns, stats.BufferedMs, stats.IsPlaybackActive);
            }
        }
        else if (!_hasLoggedStartupDiscard)
        {
            // One-time, non-alarming: the SDK aligned playback to the schedule by discarding stale samples.
            _hasLoggedStartupDiscard = true;
            var approxMs = newDrops * 1000.0 / (_sampleRate * _channels);
            _logger.LogInformation(
                "Startup alignment discard: SDK dropped {Dropped} samples (~{Ms:F0}ms) aligning playback to the schedule. " +
                "bufferedMs={BufferedMs:F0}, overrunCount={Overruns} (no overrun - buffer not full, Read consuming). " +
                "One-time startup transient.",
                newDrops, approxMs, stats.BufferedMs, currentOverruns);
        }

        _lastKnownDroppedSamples = currentDropped;
        _lastKnownOverrunCount = currentOverruns;
    }

    /// <summary>
    /// Resets diagnostic state. Call when buffer is cleared or playback restarts.
    /// </summary>
    public void Reset()
    {
        _hasLoggedOverrunStart = false;  // Allow ERROR level logging on next overrun
        _hasLoggedStartupDiscard = false;  // Allow startup-discard INFO on next playback
    }
}
