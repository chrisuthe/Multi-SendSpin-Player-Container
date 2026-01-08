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

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the underlying timed audio buffer.
    /// </summary>
    public ITimedAudioBuffer Buffer => _buffer;

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

        // Read remaining samples from the timed buffer
        // Using Read() instead of ReadRaw() - Read() applies internal sync correction
        // and has the same scheduled start logic but is proven to work on the Windows client.
        // ReadRaw() was causing playback to never start for unknown reasons.
        var remainingCount = count - insertedSamples;
        var span = buffer.AsSpan(offset + insertedSamples, remainingCount);
#pragma warning disable CS0618 // Type or member is obsolete
        var read = _buffer.Read(span, currentTime);
#pragma warning restore CS0618

        // Note: We're NOT applying our own frame correction since Read() handles it internally
        // ApplyFrameCorrection is only for ReadRaw() with external correction

        if (read == 0 && _logger != null)
        {
            // Debug: Log when Read returns 0 (rate limited)
            if (currentTime - _lastDebugLogTime >= DebugLogIntervalMicroseconds)
            {
                _lastDebugLogTime = currentTime;
                var stats = _buffer.GetStats();
                _logger.LogWarning(
                    "Read returned 0: currentTime={CurrentTime}μs, bufferedMs={BufferedMs:F0}, " +
                    "isPlaybackActive={IsPlaybackActive}, syncError={SyncError:F0}μs",
                    currentTime,
                    stats.BufferedMs,
                    stats.IsPlaybackActive,
                    stats.SyncErrorMicroseconds);
            }
        }

        // Fill remainder with silence if underrun
        var totalSamples = insertedSamples + read;
        if (totalSamples < count)
        {
            buffer.AsSpan(offset + totalSamples, count - totalSamples).Fill(0f);
        }

        // Always return requested count to keep audio output happy
        return count;
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
