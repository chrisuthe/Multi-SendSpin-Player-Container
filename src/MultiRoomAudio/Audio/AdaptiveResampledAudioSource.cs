using System.Buffers;
using Microsoft.Extensions.Logging;
using MultiRoomAudio.Audio.LibSampleRate;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/> using adaptive resampling
/// for clock drift compensation. Unlike <see cref="BufferedAudioSampleSource"/> which uses frame drop/insert,
/// this implementation continuously adjusts the resampling ratio to maintain synchronization.
/// </summary>
/// <remarks>
/// <para><strong>Why Adaptive Resampling?</strong></para>
/// <para>
/// Frame drop/insert correction works well on stable hardware but can cause audible warbling on
/// virtualized environments (VMs) where timing jitter causes rapid oscillation between dropping
/// and inserting frames. Adaptive resampling spreads corrections across every sample, making
/// them completely inaudible while achieving tighter synchronization.
/// </para>
///
/// <para><strong>How It Works</strong></para>
/// <para>
/// A PLL-like control loop continuously measures sync error and adjusts the resampling ratio:
/// </para>
/// <list type="bullet">
///   <item><description>Behind schedule (positive error): ratio &lt; 1.0 (consume input faster to catch up)</description></item>
///   <item><description>Ahead of schedule (negative error): ratio > 1.0 (consume input slower to let buffer fill)</description></item>
///   <item><description>Ratio changes are smoothed to prevent audible pitch wobble</description></item>
/// </list>
///
/// <para><strong>Performance</strong></para>
/// <para>
/// Uses libsamplerate (Secret Rabbit Code) with SINC_MEDIUM_QUALITY for good quality/CPU balance.
/// Typical ratio adjustments are ±0.1% (1000 ppm), well within libsamplerate's capabilities.
/// </para>
/// </remarks>
public sealed class AdaptiveResampledAudioSource : IAudioSampleSource, IDisposable
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly Func<long> _getCurrentTimeMicroseconds;
    private readonly Func<(double DriftPpm, bool IsReliable)>? _getDriftRate;
    private readonly ILogger<AdaptiveResampledAudioSource>? _logger;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly AdaptiveSampleRateConverter _resampler;

    // Intermediate buffer for reading from SDK (before resampling)
    // We need slightly more input samples when ratio < 1.0 (speeding up = consuming more)
    private const double MaxRatioDeviation = 0.02; // 2% margin for buffer sizing
    private float[]? _inputBuffer;

    // Leftover buffer for samples not consumed by the resampler
    // libsamplerate may not use all input due to filter delay or ratio changes
    private float[]? _leftoverBuffer;
    private int _leftoverCount;

    // Debug logging rate limiter
    private long _lastDebugLogTime;
    private const long DebugLogIntervalMicroseconds = 1_000_000; // 1 second

    // Diagnostic counters
    private long _totalReads;
    private long _zeroReads;
    private long _successfulReads;
    private long _firstReadTime;
    private long _lastSuccessfulReadTime;
    private bool _hasEverReceivedSamples;

    // Overrun tracking
    private long _lastKnownDroppedSamples;
    private long _lastKnownOverrunCount;
    private bool _hasLoggedOverrunStart;

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the underlying timed audio buffer.
    /// </summary>
    public ITimedAudioBuffer Buffer => _buffer;

    /// <summary>
    /// Gets the adaptive resampler for stats access.
    /// </summary>
    public AdaptiveSampleRateConverter Resampler => _resampler;

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
    /// Current resampling ratio. 1.0 = no change, &lt;1.0 = speeding up (catching up), >1.0 = slowing down.
    /// </summary>
    public double CurrentResampleRatio => _resampler.CurrentRatio;

    /// <summary>
    /// Total samples dropped for sync correction. Always 0 for adaptive resampling.
    /// </summary>
    public long TotalDropped => 0;

    /// <summary>
    /// Total samples inserted for sync correction. Always 0 for adaptive resampling.
    /// </summary>
    public long TotalInserted => 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdaptiveResampledAudioSource"/> class.
    /// </summary>
    /// <param name="buffer">The timed audio buffer to read from.</param>
    /// <param name="getCurrentTimeMicroseconds">Function that returns current local time in microseconds.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="getDriftRate">Optional function to get drift rate from Kalman filter for stable correction.</param>
    /// <param name="resamplerQuality">libsamplerate quality setting (default: SINC_MEDIUM_QUALITY).</param>
    public AdaptiveResampledAudioSource(
        ITimedAudioBuffer buffer,
        Func<long> getCurrentTimeMicroseconds,
        ILogger<AdaptiveResampledAudioSource>? logger = null,
        Func<(double DriftPpm, bool IsReliable)>? getDriftRate = null,
        int resamplerQuality = SampleRateInterop.ConverterType.SincMediumQuality)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(getCurrentTimeMicroseconds);

        _buffer = buffer;
        _getCurrentTimeMicroseconds = getCurrentTimeMicroseconds;
        _getDriftRate = getDriftRate;
        _logger = logger;
        _channels = buffer.Format.Channels;
        _sampleRate = buffer.Format.SampleRate;

        if (_channels <= 0)
        {
            throw new ArgumentException("Audio format must have at least one channel.", nameof(buffer));
        }

        // Create the adaptive resampler
        _resampler = new AdaptiveSampleRateConverter(
            _channels,
            resamplerQuality,
            logger as ILogger);

        _logger?.LogInformation(
            "AdaptiveResampledAudioSource initialized: channels={Channels}, sampleRate={SampleRate}, " +
            "driftRateAvailable={DriftRateAvailable}, using libsamplerate adaptive resampling",
            _channels, _sampleRate, getDriftRate != null);
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

        // Get drift rate from Kalman filter (if available) for stable correction
        if (_getDriftRate != null)
        {
            var (driftPpm, isReliable) = _getDriftRate();
            _resampler.UpdateDriftRate(driftPpm, isReliable);
        }

        // Get current sync error and update resampler ratio
        var syncError = _buffer.SmoothedSyncErrorMicroseconds;
        _resampler.UpdateSyncError((long)syncError);

        // Calculate how many input samples we need based on current ratio
        // If ratio < 1.0 (speeding up/catching up), we need more input samples
        // If ratio > 1.0 (slowing down), we need fewer input samples
        var outputFrames = count / _channels;
        var ratio = _resampler.CurrentRatio;

        // Add margin for ratio changes and resampler internal buffering
        var inputFramesNeeded = (int)Math.Ceiling(outputFrames / ratio * (1.0 + MaxRatioDeviation)) + 16;
        var inputSamplesNeeded = inputFramesNeeded * _channels;

        // Account for leftover samples from previous call
        var samplesToReadFromBuffer = Math.Max(0, inputSamplesNeeded - _leftoverCount);

        // Ensure input buffer is large enough for leftovers + new samples
        EnsureInputBuffer(inputSamplesNeeded);

        // Copy leftover samples to the beginning of input buffer
        if (_leftoverCount > 0 && _leftoverBuffer != null)
        {
            _leftoverBuffer.AsSpan(0, _leftoverCount).CopyTo(_inputBuffer.AsSpan());
        }

        // Read raw samples from the timed buffer (after leftovers)
        var rawRead = 0;
        if (samplesToReadFromBuffer > 0)
        {
            rawRead = _buffer.ReadRaw(
                _inputBuffer.AsSpan(_leftoverCount, samplesToReadFromBuffer),
                currentTime);
        }

        var totalInputSamples = _leftoverCount + rawRead;
        _leftoverCount = 0; // Will be set again if there are leftovers

        if (totalInputSamples > 0)
        {
            if (rawRead > 0)
            {
                _successfulReads++;
                _lastSuccessfulReadTime = currentTime;

                // Log first successful read
                if (!_hasEverReceivedSamples)
                {
                    _hasEverReceivedSamples = true;
                    var elapsedMs = (currentTime - _firstReadTime) / 1000.0;
                    _logger?.LogInformation(
                        "First samples received (adaptive): elapsedMs={ElapsedMs:F1}, " +
                        "totalReads={TotalReads}, zeroReads={ZeroReads}",
                        elapsedMs, _totalReads, _zeroReads);
                }
            }

            // Resample the audio
            var outputFramesGen = _resampler.Process(
                _inputBuffer.AsSpan(0, totalInputSamples),
                buffer.AsSpan(offset, count),
                out var inputFramesUsed);

            var outputSamples = outputFramesGen * _channels;
            var inputSamplesUsed = inputFramesUsed * _channels;

            // Save unused samples as leftovers for next call
            // This prevents audio discontinuities when libsamplerate doesn't use all input
            var unusedSamples = totalInputSamples - inputSamplesUsed;
            if (unusedSamples > 0)
            {
                EnsureLeftoverBuffer(unusedSamples);
                _inputBuffer.AsSpan(inputSamplesUsed, unusedSamples).CopyTo(_leftoverBuffer.AsSpan());
                _leftoverCount = unusedSamples;
            }

            // Fill remainder with silence if needed
            if (outputSamples < count)
            {
                buffer.AsSpan(offset + outputSamples, count - outputSamples).Fill(0f);
            }

            // Periodic debug logging
            LogDebugInfo(currentTime, syncError, outputSamples);
        }
        else
        {
            _zeroReads++;
            LogZeroRead(currentTime);

            // Fill with silence
            buffer.AsSpan(offset, count).Fill(0f);
        }

        // Check for overruns (SDK dropping samples due to buffer full)
        CheckForOverruns();

        // Always return requested count to keep audio output happy
        return count;
    }

    /// <summary>
    /// Resets the source state. Call when starting a new audio stream.
    /// Preserves drift rate from Kalman filter for faster re-lock after reanchoring.
    /// </summary>
    public void Reset()
    {
        // Preserve drift knowledge so we don't have to wait for Kalman to reconverge
        // This enables faster audio sync recovery after reanchoring
        _resampler.Reset(preserveDrift: true);
        _totalReads = 0;
        _zeroReads = 0;
        _successfulReads = 0;
        _firstReadTime = 0;
        _lastSuccessfulReadTime = 0;
        _hasEverReceivedSamples = false;
        _lastKnownDroppedSamples = 0;
        _lastKnownOverrunCount = 0;
        _hasLoggedOverrunStart = false;
        _leftoverCount = 0;

        _logger?.LogDebug("AdaptiveResampledAudioSource reset (drift preserved)");
    }

    private void EnsureInputBuffer(int minSize)
    {
        if (_inputBuffer == null || _inputBuffer.Length < minSize)
        {
            // Round up to power of 2 for efficiency
            var newSize = Math.Max(minSize, 4096);
            newSize = (int)Math.Pow(2, Math.Ceiling(Math.Log2(newSize)));
            _inputBuffer = new float[newSize];
        }
    }

    private void EnsureLeftoverBuffer(int minSize)
    {
        if (_leftoverBuffer == null || _leftoverBuffer.Length < minSize)
        {
            // Round up to power of 2 for efficiency
            var newSize = Math.Max(minSize, 1024);
            newSize = (int)Math.Pow(2, Math.Ceiling(Math.Log2(newSize)));
            _leftoverBuffer = new float[newSize];
        }
    }

    private void LogZeroRead(long currentTime)
    {
        // Rate-limit zero read logging
        if (currentTime - _lastDebugLogTime < DebugLogIntervalMicroseconds)
            return;

        _lastDebugLogTime = currentTime;

        var stats = _buffer.GetStats();
        var bufferedMs = stats.BufferedMs;
        var syncError = _buffer.SmoothedSyncErrorMicroseconds / 1000.0;

        _logger?.LogDebug(
            "Zero read from buffer: bufferedMs={BufferedMs:F1}, syncError={SyncError:F2}ms, " +
            "ratio={Ratio:F6}, totalReads={TotalReads}, zeroReads={ZeroReads}",
            bufferedMs, syncError, _resampler.CurrentRatio, _totalReads, _zeroReads);
    }

    private void LogDebugInfo(long currentTime, double syncError, int outputSamples)
    {
        // Rate-limit debug logging
        if (currentTime - _lastDebugLogTime < DebugLogIntervalMicroseconds)
            return;

        _lastDebugLogTime = currentTime;

        var syncErrorMs = syncError / 1000.0;
        var ratioPpm = (_resampler.CurrentRatio - 1.0) * 1_000_000;
        var driftPpm = _resampler.DriftRatePpm;
        var driftReliable = _resampler.IsDriftReliable;

        _logger?.LogDebug(
            "Adaptive resample: syncError={SyncError:F2}ms, drift={DriftPpm:+0;-0}ppm (reliable={Reliable}), " +
            "ratio={Ratio:F6} ({RatioPpm:+0;-0}ppm), output={Output} samples",
            syncErrorMs, driftPpm, driftReliable, _resampler.CurrentRatio, ratioPpm, outputSamples);
    }

    private void CheckForOverruns()
    {
        var stats = _buffer.GetStats();
        var currentDropped = stats.DroppedSamples;
        var currentOverruns = stats.OverrunCount;

        // Detect start of overrun condition
        if (currentOverruns > _lastKnownOverrunCount)
        {
            if (!_hasLoggedOverrunStart)
            {
                _hasLoggedOverrunStart = true;
                _logger?.LogError(
                    "SDK buffer overrun detected - samples being dropped. " +
                    "This may indicate playback stall. droppedSamples={Dropped}, overruns={Overruns}",
                    currentDropped, currentOverruns);
            }
            else
            {
                // Subsequent overruns - log at debug level
                _logger?.LogDebug(
                    "Ongoing overrun: droppedSamples={Dropped}, overruns={Overruns}",
                    currentDropped, currentOverruns);
            }
        }
        else if (_hasLoggedOverrunStart && currentDropped == _lastKnownDroppedSamples)
        {
            // Overrun stopped
            _hasLoggedOverrunStart = false;
            _logger?.LogInformation(
                "Overrun condition cleared. Total samples dropped: {Dropped}",
                currentDropped);
        }

        _lastKnownDroppedSamples = currentDropped;
        _lastKnownOverrunCount = currentOverruns;
    }

    public void Dispose()
    {
        _resampler.Dispose();
        _logger?.LogDebug(
            "AdaptiveResampledAudioSource disposed: totalReads={TotalReads}, " +
            "successfulReads={SuccessfulReads}, zeroReads={ZeroReads}",
            _totalReads, _successfulReads, _zeroReads);
    }
}
