using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/>.
/// Provides current local time to the buffer for timed sample release and
/// implements player-controlled sync correction via frame drop/insert.
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
/// </para>
/// </remarks>
public sealed class BufferedAudioSampleSource : IAudioSampleSource
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly Func<long> _getCurrentTimeMicroseconds;
    private readonly ILogger<BufferedAudioSampleSource>? _logger;

    // Correction threshold - within 5ms is acceptable, beyond that we correct
    private const long CorrectionThresholdMicroseconds = 5_000;  // 5ms deadband

    // Apply correction every N frames to spread out the corrections
    private const int CorrectionIntervalFrames = 100;

    // Frame tracking for corrections
    private int _frameCounter;
    private readonly float[] _lastFrame;
    private readonly float[] _dropBuffer;
    private readonly int _channels;

    // Pending insertion - set when we need to insert on next read
    private bool _pendingInsertion;

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

        if (_channels <= 0)
        {
            throw new ArgumentException("Audio format must have at least one channel.", nameof(buffer));
        }

        // Pre-allocate buffers to avoid GC pressure on audio thread
        _lastFrame = new float[_channels];
        _dropBuffer = new float[_channels];
    }

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
        var currentTime = _getCurrentTimeMicroseconds();
        _totalReads++;

        // Track first read time for diagnostics
        if (_firstReadTime == 0)
        {
            _firstReadTime = currentTime;
        }

        // Handle pending frame insertion from previous correction decision
        int insertedSamples = 0;
        if (_pendingInsertion && count >= _channels)
        {
            // Insert the saved frame at the start of the buffer
            Array.Copy(_lastFrame, 0, buffer, offset, _channels);
            insertedSamples = _channels;
            _pendingInsertion = false;

            // Notify SDK that we inserted samples (output without consuming)
            _buffer.NotifyExternalCorrection(0, _channels);
        }

        // Read remaining samples from the timed buffer using ReadRaw()
        // This gives us raw samples without SDK correction - we handle correction ourselves
        var remainingCount = count - insertedSamples;
        var span = buffer.AsSpan(offset + insertedSamples, remainingCount);
        var read = _buffer.ReadRaw(span, currentTime);

        // Apply our own frame drop/insert correction based on sync error
        if (read > 0)
        {
            ApplyFrameCorrection(buffer, offset + insertedSamples, read);
        }

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

            // Debug: Log when Read returns 0 (rate limited)
            if (_logger != null && currentTime - _lastDebugLogTime >= DebugLogIntervalMicroseconds)
            {
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

                // Additional detailed log for buffer state
                _logger.LogWarning(
                    "Buffer state: samplesWritten={Written}, samplesRead={Read}, " +
                    "droppedOverflow={DroppedOverflow}, droppedSync={DroppedSync}, insertedSync={InsertedSync}",
                    stats.TotalSamplesWritten,
                    stats.TotalSamplesRead,
                    stats.DroppedSamples,
                    stats.SamplesDroppedForSync,
                    stats.SamplesInsertedForSync);
            }
        }

        // Fill remainder with silence if underrun
        var totalSamples = insertedSamples + read;
        if (totalSamples < count)
        {
            buffer.AsSpan(offset + totalSamples, count - totalSamples).Fill(0f);
        }

        // Check for overruns (SDK dropping samples due to buffer full)
        // This happens when Read() isn't consuming samples fast enough (or at all)
        CheckForOverruns();

        // Always return requested count to keep audio output happy
        return count;
    }

    /// <summary>
    /// Checks if the SDK has started dropping samples due to buffer overflow.
    /// Logs a warning when overruns are first detected and periodically thereafter.
    /// </summary>
    private void CheckForOverruns()
    {
        if (_logger == null) return;

        var stats = _buffer.GetStats();
        var currentDropped = stats.DroppedSamples;
        var currentOverruns = stats.OverrunCount;

        // Detect new overruns
        if (currentDropped > _lastKnownDroppedSamples || currentOverruns > _lastKnownOverrunCount)
        {
            var newDrops = currentDropped - _lastKnownDroppedSamples;
            var newOverruns = currentOverruns - _lastKnownOverrunCount;

            // Log first occurrence with full context
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
                // Log periodic updates when drops are significant
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
    /// Applies frame drop/insert correction based on current sync error.
    /// </summary>
    private void ApplyFrameCorrection(float[] buffer, int offset, int sampleCount)
    {
        // Get current sync error (smoothed for stable decisions)
        var syncError = _buffer.SmoothedSyncErrorMicroseconds;
        var absError = Math.Abs(syncError);

        // Save the last frame for potential insertion
        SaveLastFrame(buffer, offset, sampleCount);

        // No correction needed if within 5ms deadband
        if (absError < CorrectionThresholdMicroseconds)
        {
            return;
        }

        // Increment frame counter
        _frameCounter++;

        // Apply correction periodically (not every frame - spread them out)
        if (_frameCounter < CorrectionIntervalFrames)
        {
            return;
        }
        _frameCounter = 0;

        if (syncError > 0)
        {
            // Behind schedule (positive error) - DROP a frame to catch up
            // We'll read and discard an extra frame's worth of samples
            DropFrame();
        }
        else
        {
            // Ahead of schedule (negative error) - INSERT a frame to slow down
            // Schedule insertion for next read (deferred because we already read this buffer)
            InsertFrame();
        }
    }

    /// <summary>
    /// Saves the last frame for potential insertion.
    /// </summary>
    private void SaveLastFrame(float[] buffer, int offset, int sampleCount)
    {
        if (sampleCount < _channels)
        {
            return;
        }

        // Save the last frame (last _channels samples)
        var lastFrameStart = offset + sampleCount - _channels;
        Array.Copy(buffer, lastFrameStart, _lastFrame, 0, _channels);
    }

    /// <summary>
    /// Drops a frame by reading and discarding samples from the buffer.
    /// </summary>
    private void DropFrame()
    {
        // Read an extra frame's worth into pre-allocated buffer and discard it
        // This advances the buffer cursor, making us catch up
        var currentTime = _getCurrentTimeMicroseconds();
        var dropped = _buffer.ReadRaw(_dropBuffer.AsSpan(), currentTime);

        if (dropped > 0)
        {
            // Notify the buffer that we dropped samples
            _buffer.NotifyExternalCorrection(dropped, 0);
        }
    }

    /// <summary>
    /// Schedules a frame insertion for the next read.
    /// The insertion is deferred because we've already read this buffer's samples.
    /// </summary>
    private void InsertFrame()
    {
        // Set flag - next Read() will insert the saved frame before reading
        // This causes us to output more samples than we consume, slowing playback
        _pendingInsertion = true;
    }
}
