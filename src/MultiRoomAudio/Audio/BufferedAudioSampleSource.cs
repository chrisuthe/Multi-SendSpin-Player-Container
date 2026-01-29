using System.Buffers;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/>.
/// Provides current local time to the buffer for timed sample release and
/// implements player-controlled sync correction via frame drop/insert with interpolation.
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
///     <see cref="Reset"/> may be called from any thread to reset correction state. It modifies
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
/// <para><strong>Sync Correction Algorithm</strong></para>
/// <para>
/// The Sendspin protocol delivers audio samples with precise timestamps indicating when each
/// sample should be played. Network jitter, clock drift between sender/receiver, and audio
/// hardware variations can cause the playback position to drift from the ideal schedule.
/// This class measures and corrects that drift.
/// </para>
/// <para>
/// <strong>Algorithm overview:</strong>
/// </para>
/// <list type="number">
///   <item><description>
///     The SDK's <see cref="ITimedAudioBuffer"/> measures sync error: the difference between
///     where playback should be (based on timestamps) and where it actually is.
///     Positive error means playback is behind; negative means it's ahead.
///   </description></item>
///   <item><description>
///     Uses hysteresis to prevent oscillation: enters correction mode when error exceeds 15ms,
///     exits when error drops below 3ms. This prevents rapid switching between drop/insert.
///   </description></item>
///   <item><description>
///     Beyond the deadband, we apply correction by dropping or inserting frames:
///     <list type="bullet">
///       <item><description>Behind schedule (positive error): DROP frames to catch up faster</description></item>
///       <item><description>Ahead of schedule (negative error): INSERT frames to slow down</description></item>
///     </list>
///   </description></item>
///   <item><description>
///     Correction rate matches the SDK's TimedAudioBuffer.UpdateCorrectionRate() formula:
///     larger errors trigger more frequent corrections, capped at 4% of sample rate.
///   </description></item>
///   <item><description>
///     To minimize audible artifacts, corrections use linear interpolation:
///     <list type="bullet">
///       <item><description>Drop: blend two frames into one: (A + B) / 2</description></item>
///       <item><description>Insert: interpolate between last output and next input: (last + next) / 2</description></item>
///     </list>
///   </description></item>
///   <item><description>
///     The SDK is notified of all corrections via <see cref="ITimedAudioBuffer.NotifyExternalCorrection"/>
///     so it can maintain accurate sync tracking.
///   </description></item>
/// </list>
///
/// <para><strong>Performance Considerations</strong></para>
/// <para>
/// The <see cref="Read"/> method is called from a real-time audio thread. To avoid glitches:
/// </para>
/// <list type="bullet">
///   <item><description>Uses <see cref="System.Buffers.ArrayPool{T}"/> to avoid GC allocations</description></item>
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

    // Correction thresholds with hysteresis to prevent oscillation.
    // Entry threshold set high (50ms) to tolerate VM jitter - on Proxmox VMs,
    // pa_stream_get_latency() returns noisy values (±20-50ms). Lower thresholds
    // cause oscillating corrections that produce audible warbling.
    // Exit threshold wider (10ms) for stability once corrections stop.
    private const long EntryThresholdMicroseconds = 50_000;  // 50ms - only correct large drift
    private const long ExitThresholdMicroseconds = 10_000;   // 10ms - wider exit band

    // Correction rate constants matching SDK's TimedAudioBuffer.UpdateCorrectionRate()
    private const double CorrectionTargetSeconds = 2.0;  // Time to eliminate error (CLI default)
    private const double MaxSpeedCorrection = 0.04;      // 4% max correction rate (CLI default)

    // State machine for correction mode - prevents oscillation between drop/insert
    private enum CorrectionMode { Idle, Dropping, Inserting }
    private CorrectionMode _currentMode = CorrectionMode.Idle;

    // Frame tracking for corrections
    private int _framesSinceLastCorrection;
    private float[]? _lastOutputFrame;

    // Debug logging rate limiter
    private long _lastDebugLogTime;
    private const long DebugLogIntervalMicroseconds = 1_000_000; // 1 second

    // Overrun check rate limiter - avoid GetStats() lock acquisition in audio hot path
    private long _lastOverrunCheckTime;
    private const long OverrunCheckIntervalMicroseconds = 1_000_000; // 1 second

    // Diagnostic counters for tracking buffer behavior
    private long _totalReads;
    private long _zeroReads;
    private long _successfulReads;
    private long _firstReadTime;
    private long _lastSuccessfulReadTime;
    private bool _hasEverReceivedSamples;

    // Correction tracking for stats
    private long _totalDropped;
    private long _totalInserted;

    // Overrun tracking - detect when SDK starts dropping samples
    private long _lastKnownDroppedSamples;
    private long _lastKnownOverrunCount;
    private bool _hasLoggedOverrunStart;

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
    /// <summary>Total samples dropped for sync correction.</summary>
    public long TotalDropped => _totalDropped;
    /// <summary>Total samples inserted for sync correction.</summary>
    public long TotalInserted => _totalInserted;

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
            "interpolation=3-point weighted with 2-point fallback",
            _channels, _sampleRate);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <see cref="ArrayPool{T}.Shared"/> to avoid allocating temporary buffers on every
    /// audio callback. Audio threads are real-time sensitive, and GC pauses from frequent
    /// allocations can cause audible glitches.
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

        // Initialize last output frame if needed
        _lastOutputFrame ??= new float[_channels];

        // Rent a buffer from the pool to avoid GC allocations in the audio thread
        var tempBuffer = ArrayPool<float>.Shared.Rent(count);
        try
        {
            // Read raw samples from the timed buffer (no SDK correction)
            var rawRead = _buffer.ReadRaw(tempBuffer.AsSpan(0, count), currentTime);

            if (rawRead > 0)
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

                // Apply correction and copy to output
                var (outputCount, dropped, inserted) = ApplyCorrectionWithInterpolation(
                    tempBuffer, rawRead, buffer.AsSpan(offset, count));

                // Notify SDK of corrections for accurate sync tracking
                if (dropped > 0 || inserted > 0)
                {
                    _buffer.NotifyExternalCorrection(dropped, inserted);
                    _totalDropped += dropped;
                    _totalInserted += inserted;
                }

                // Fill remainder with silence if needed
                if (outputCount < count)
                {
                    buffer.AsSpan(offset + outputCount, count - outputCount).Fill(0f);
                }
            }
            else
            {
                _zeroReads++;
                LogZeroRead(currentTime);

                // Fill with silence
                buffer.AsSpan(offset, count).Fill(0f);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tempBuffer, clearArray: false);
        }

        // Check for overruns (SDK dropping samples due to buffer full)
        // Rate-limited to avoid GetStats() lock acquisition in audio hot path
        if (currentTime - _lastOverrunCheckTime >= OverrunCheckIntervalMicroseconds)
        {
            _lastOverrunCheckTime = currentTime;
            CheckForOverruns();
        }

        // Always return requested count to keep audio output happy
        return count;
    }

    /// <summary>
    /// Calculates the correction interval based on sync error magnitude.
    /// Uses the same formula as SDK's TimedAudioBuffer.UpdateCorrectionRate() for consistency.
    /// </summary>
    /// <param name="absErrorMicroseconds">Absolute sync error in microseconds.</param>
    /// <returns>Number of frames between corrections.</returns>
    private int CalculateCorrectionInterval(long absErrorMicroseconds)
    {
        // Match SDK's UpdateCorrectionRate() calculation:
        // 1. Convert error to frames: framesError = absError * sampleRate / 1_000_000
        // 2. Calculate corrections per second to eliminate error in CorrectionTargetSeconds
        // 3. Cap at MaxSpeedCorrection * sampleRate
        // 4. Convert to interval: sampleRate / correctionsPerSec
        // 5. Minimum interval = channels * 10 (matches SDK)
        var framesError = absErrorMicroseconds * _sampleRate / 1_000_000.0;
        var desiredCorrectionsPerSec = framesError / CorrectionTargetSeconds;
        var maxCorrectionsPerSec = (double)_sampleRate * MaxSpeedCorrection;
        var actualCorrectionsPerSec = Math.Min(desiredCorrectionsPerSec, maxCorrectionsPerSec);

        if (actualCorrectionsPerSec <= 0)
        {
            return 500; // Very gentle correction
        }

        var interval = (int)((double)_sampleRate / actualCorrectionsPerSec);
        return Math.Max(interval, _channels * 10);  // Min interval matches SDK
    }

    /// <summary>
    /// Applies sync correction with interpolation to minimize audible artifacts.
    /// Uses a hysteresis state machine to prevent oscillation between dropping and inserting.
    /// Uses 3-point weighted interpolation when sufficient lookahead is available in the input buffer,
    /// falling back to 2-point linear interpolation otherwise.
    /// </summary>
    /// <returns>Tuple of (output sample count, samples dropped, samples inserted).</returns>
    private (int OutputCount, int SamplesDropped, int SamplesInserted) ApplyCorrectionWithInterpolation(
        float[] input, int inputCount, Span<float> output)
    {
        var syncError = _buffer.SmoothedSyncErrorMicroseconds;
        var absError = Math.Abs((long)syncError);

        // Update state machine with hysteresis to prevent oscillation.
        // Transitions: Idle→Dropping, Idle→Inserting, Dropping→Idle, Inserting→Idle
        // Direct Dropping↔Inserting transitions are NOT allowed (must go through Idle)
        // Also exits to Idle if sync error sign flips (overshoot detection)
        switch (_currentMode)
        {
            case CorrectionMode.Idle:
                if (syncError > EntryThresholdMicroseconds)
                {
                    _currentMode = CorrectionMode.Dropping;
                }
                else if (syncError < -EntryThresholdMicroseconds)
                {
                    _currentMode = CorrectionMode.Inserting;
                }
                break;

            case CorrectionMode.Dropping:
                // Exit to Idle when error is small OR sign flipped (we overshot)
                // Sign flip: we were behind (positive error) but now ahead (negative)
                if (absError < ExitThresholdMicroseconds || syncError < 0)
                {
                    _currentMode = CorrectionMode.Idle;
                }
                break;

            case CorrectionMode.Inserting:
                // Exit to Idle when error is small OR sign flipped (we overshot)
                // Sign flip: we were ahead (negative error) but now behind (positive)
                if (absError < ExitThresholdMicroseconds || syncError > 0)
                {
                    _currentMode = CorrectionMode.Idle;
                }
                break;
        }

        // If in Idle mode, just copy input to output (no correction)
        if (_currentMode == CorrectionMode.Idle)
        {
            var toCopy = Math.Min(inputCount, output.Length);
            input.AsSpan(0, toCopy).CopyTo(output);

            // Save last frame for potential future corrections
            if (toCopy >= _channels)
            {
                input.AsSpan(toCopy - _channels, _channels).CopyTo(_lastOutputFrame);
            }

            return (toCopy, 0, 0);
        }

        // Calculate correction rate based on error magnitude
        var correctionInterval = CalculateCorrectionInterval(absError);

        // Process frame by frame
        var inputPos = 0;
        var outputPos = 0;
        var samplesDropped = 0;
        var samplesInserted = 0;

        while (outputPos < output.Length)
        {
            var remainingInput = inputCount - inputPos;
            _framesSinceLastCorrection++;

            // Check if we should DROP a frame (read two, output one interpolated)
            if (_currentMode == CorrectionMode.Dropping && _framesSinceLastCorrection >= correctionInterval)
            {
                _framesSinceLastCorrection = 0;

                if (remainingInput >= _channels * 2)
                {
                    var frameAStart = inputPos;
                    var frameBStart = inputPos + _channels;
                    var outputSpan = output.Slice(outputPos, _channels);

                    // Use 3-point weighted interpolation if we have lookahead available
                    if (remainingInput >= _channels * 3)
                    {
                        // 3-point weighted: A=0.25, B=0.5, C=0.25 (Gaussian-like kernel)
                        // Smoother blend that considers the frame after the drop point
                        var frameCStart = inputPos + _channels * 2;
                        for (int i = 0; i < _channels; i++)
                        {
                            outputSpan[i] = input[frameAStart + i] * 0.25f
                                          + input[frameBStart + i] * 0.5f
                                          + input[frameCStart + i] * 0.25f;
                        }
                    }
                    else
                    {
                        // Fallback: 2-point linear interpolation (A + B) / 2
                        for (int i = 0; i < _channels; i++)
                        {
                            outputSpan[i] = (input[frameAStart + i] + input[frameBStart + i]) * 0.5f;
                        }
                    }

                    // Consume both input frames (A and B)
                    inputPos += _channels * 2;

                    // Save as last output frame
                    outputSpan.CopyTo(_lastOutputFrame);

                    outputPos += _channels;
                    samplesDropped += _channels;
                    continue;
                }
            }

            // Check if we should INSERT a frame (output interpolated without consuming)
            if (_currentMode == CorrectionMode.Inserting && _framesSinceLastCorrection >= correctionInterval)
            {
                _framesSinceLastCorrection = 0;

                if (output.Length - outputPos >= _channels)
                {
                    var outputSpan = output.Slice(outputPos, _channels);

                    // Use true lookahead if we have two frames available
                    if (remainingInput >= _channels * 2)
                    {
                        // Interpolate between current and next frame (true lookahead)
                        // Better than using stale _lastOutputFrame from previous callback
                        var currentStart = inputPos;
                        var nextStart = inputPos + _channels;
                        for (int i = 0; i < _channels; i++)
                        {
                            outputSpan[i] = (input[currentStart + i] + input[nextStart + i]) * 0.5f;
                        }

                        // Save interpolated frame
                        outputSpan.CopyTo(_lastOutputFrame);
                    }
                    else if (remainingInput >= _channels)
                    {
                        // Fallback: use last output frame + current input
                        for (int i = 0; i < _channels; i++)
                        {
                            outputSpan[i] = (_lastOutputFrame![i] + input[inputPos + i]) * 0.5f;
                        }

                        // Save interpolated frame
                        outputSpan.CopyTo(_lastOutputFrame);
                    }
                    else
                    {
                        // Fallback: duplicate last frame
                        _lastOutputFrame.AsSpan().CopyTo(outputSpan);
                    }

                    outputPos += _channels;
                    samplesInserted += _channels;
                    continue;
                }
            }

            // Normal frame: copy from input to output
            if (remainingInput < _channels)
            {
                break; // No more input
            }

            if (output.Length - outputPos < _channels)
            {
                break; // No more output space
            }

            var frameSpan = output.Slice(outputPos, _channels);
            input.AsSpan(inputPos, _channels).CopyTo(frameSpan);
            inputPos += _channels;

            // Save as last output frame
            frameSpan.CopyTo(_lastOutputFrame);
            outputPos += _channels;
        }

        return (outputPos, samplesDropped, samplesInserted);
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

        if (currentDropped > _lastKnownDroppedSamples || currentOverruns > _lastKnownOverrunCount)
        {
            var newDrops = currentDropped - _lastKnownDroppedSamples;
            var newOverruns = currentOverruns - _lastKnownOverrunCount;

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

            _lastKnownDroppedSamples = currentDropped;
            _lastKnownOverrunCount = currentOverruns;
        }
    }

    /// <summary>
    /// Resets correction state. Call when buffer is cleared or playback restarts.
    /// </summary>
    public void Reset()
    {
        _framesSinceLastCorrection = 0;
        _lastOutputFrame = null;
        _totalDropped = 0;
        _totalInserted = 0;
        _currentMode = CorrectionMode.Idle;
        _hasLoggedOverrunStart = false;  // Allow ERROR level logging on next overrun
    }
}
