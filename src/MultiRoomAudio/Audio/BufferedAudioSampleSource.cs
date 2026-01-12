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
/// <para>
/// This class is called from the audio thread and must be fast and non-blocking.
/// It reads raw samples from the buffer and applies sync correction based on
/// the buffer's sync error measurements.
/// </para>
/// <para>
/// Correction strategy:
/// - Within 5ms: no correction (acceptable tolerance)
/// - Beyond 5ms behind (positive error): drop frames to catch up
/// - Beyond 5ms ahead (negative error): insert frames to slow down
/// Correction rate is proportional to error magnitude for smooth convergence.
/// </para>
/// <para>
/// Drop/insert uses linear interpolation to minimize audible artifacts:
/// - Drop: blend two frames into one ((A + B) / 2)
/// - Insert: interpolate between last output and next input ((last + next) / 2)
/// </para>
/// </remarks>
public sealed class BufferedAudioSampleSource : IAudioSampleSource
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly Func<long> _getCurrentTimeMicroseconds;
    private readonly ILogger<BufferedAudioSampleSource>? _logger;
    private readonly int _channels;
    private readonly int _sampleRate;

    // Correction threshold - within 5ms is acceptable, beyond that we correct
    private const long CorrectionThresholdMicroseconds = 5_000;  // 5ms deadband

    // Correction rate limits (frames between corrections)
    private const int MinCorrectionInterval = 10;   // Most aggressive: correct every 10 frames
    private const int MaxCorrectionInterval = 500;  // Most gentle: correct every 500 frames

    // Frame tracking for corrections
    private int _framesSinceLastCorrection;
    private float[]? _lastOutputFrame;

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
        CheckForOverruns();

        // Always return requested count to keep audio output happy
        return count;
    }

    /// <summary>
    /// Calculates the correction interval based on sync error magnitude.
    /// Larger errors result in more frequent corrections.
    /// </summary>
    /// <param name="absErrorMicroseconds">Absolute sync error in microseconds.</param>
    /// <returns>Number of frames between corrections.</returns>
    private static int CalculateCorrectionInterval(long absErrorMicroseconds)
    {
        // Formula: interval = 500000 / absError
        // At 10ms (10000μs): 500000/10000 = 50 frames
        // At 50ms (50000μs): 500000/50000 = 10 frames
        // At 5ms (5000μs): 500000/5000 = 100 frames
        if (absErrorMicroseconds <= 0)
        {
            return MaxCorrectionInterval;
        }

        var interval = (int)(500_000 / absErrorMicroseconds);
        return Math.Clamp(interval, MinCorrectionInterval, MaxCorrectionInterval);
    }

    /// <summary>
    /// Applies sync correction with linear interpolation to minimize audible artifacts.
    /// </summary>
    /// <returns>Tuple of (output sample count, samples dropped, samples inserted).</returns>
    private (int OutputCount, int SamplesDropped, int SamplesInserted) ApplyCorrectionWithInterpolation(
        float[] input, int inputCount, Span<float> output)
    {
        var syncError = _buffer.SmoothedSyncErrorMicroseconds;
        var absError = Math.Abs((long)syncError);

        // No correction needed if within deadband
        if (absError < CorrectionThresholdMicroseconds)
        {
            // Just copy input to output
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
        var shouldDrop = syncError > 0;  // Positive = behind, need to drop
        var shouldInsert = syncError < 0; // Negative = ahead, need to insert

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
            if (shouldDrop && _framesSinceLastCorrection >= correctionInterval)
            {
                _framesSinceLastCorrection = 0;

                if (remainingInput >= _channels * 2)
                {
                    // Read both frames, output interpolated blend
                    var frameAStart = inputPos;
                    var frameBStart = inputPos + _channels;
                    var outputSpan = output.Slice(outputPos, _channels);

                    // Linear interpolation: (A + B) / 2
                    for (int i = 0; i < _channels; i++)
                    {
                        outputSpan[i] = (input[frameAStart + i] + input[frameBStart + i]) * 0.5f;
                    }

                    // Consume both input frames
                    inputPos += _channels * 2;

                    // Save as last output frame
                    outputSpan.CopyTo(_lastOutputFrame);

                    outputPos += _channels;
                    samplesDropped += _channels;
                    continue;
                }
            }

            // Check if we should INSERT a frame (output interpolated without consuming)
            if (shouldInsert && _framesSinceLastCorrection >= correctionInterval)
            {
                _framesSinceLastCorrection = 0;

                if (output.Length - outputPos >= _channels)
                {
                    var outputSpan = output.Slice(outputPos, _channels);

                    // Interpolate with next input frame if available
                    if (remainingInput >= _channels)
                    {
                        // Linear interpolation: (last + next) / 2
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
        _hasLoggedOverrunStart = false;  // Allow ERROR level logging on next overrun
    }
}
