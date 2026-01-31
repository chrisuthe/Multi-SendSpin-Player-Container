using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MultiRoomAudio.Audio.LibSampleRate;

/// <summary>
/// Managed wrapper for libsamplerate with drift-based adaptive ratio control.
/// Uses the Kalman filter's drift estimate for the primary correction, with a slow
/// offset correction for residual sync error. This two-component approach provides
/// much better stability than chasing noisy sync error measurements.
/// </summary>
/// <remarks>
/// <para><strong>Two-Component Control:</strong></para>
/// <list type="number">
/// <item><description>Drift term: Direct from Kalman filter (stable, immediate)</description></item>
/// <item><description>Offset term: Very slow sync error correction (60s time constant)</description></item>
/// </list>
/// <para>Thread-safety: Single producer/consumer pattern assumed (audio callback thread).</para>
/// </remarks>
public sealed class AdaptiveSampleRateConverter : IDisposable
{
    private readonly IntPtr _state;
    private readonly int _channels;
    private readonly ILogger? _logger;
    private bool _disposed;

    // Two-component control loop parameters:
    // 1. Drift term: Uses Kalman filter's drift estimate directly (stable)
    // 2. Offset term: Very slow sync error correction (handles residual offset)

    /// <summary>
    /// Maximum deviation from 1.0 ratio (±0.5% = 5,000 ppm).
    /// Real clock drift is typically 1-100 ppm, so 0.5% is generous headroom.
    /// </summary>
    private const double MaxRatioDeviation = 0.005;

    /// <summary>
    /// Low-pass filter coefficient for smoothing ratio changes.
    /// 0.02 takes ~50 calls (~1s) to converge, preventing pitch wobble.
    /// </summary>
    private const double RatioSmoothingFactor = 0.02;

    /// <summary>
    /// Time constant for offset (sync error) correction in seconds.
    /// Very slow (60s) to avoid chasing noisy sync error measurements.
    /// The drift term handles the main correction; this just trims residual offset.
    /// </summary>
    private const double OffsetCorrectionTimeSeconds = 60.0;

    /// <summary>
    /// Deadband for offset correction in microseconds.
    /// 30ms deadband ignores typical VM/PulseAudio jitter (can be 10-60ms).
    /// Only apply offset correction for larger persistent errors.
    /// </summary>
    private const long OffsetDeadbandMicroseconds = 30000;

    /// <summary>
    /// Maximum deviation during fast acquisition mode (±2% = 20,000 ppm).
    /// Allows faster initial sync when starting playback or after reanchoring.
    /// </summary>
    private const double FastAcquisitionMaxRatio = 0.02;

    /// <summary>
    /// Number of Process calls for fast acquisition mode (~10 seconds at typical rates).
    /// At 48kHz with 1024-frame buffers, ~47 calls/second, so 500 calls ≈ 10 seconds.
    /// </summary>
    private const int FastAcquisitionCalls = 500;

    // Current state
    private double _currentRatio = 1.0;
    private double _targetRatio = 1.0;
    private long _lastSyncErrorUs;
    private long _processCallCount;

    // Drift rate from Kalman filter (the stable signal we actually want to use)
    private double _driftRatePpm;
    private bool _isDriftReliable;

    // Pinned buffers for P/Invoke (avoid repeated allocations)
    private GCHandle _inputHandle;
    private GCHandle _outputHandle;
    private float[]? _pinnedInputBuffer;
    private float[]? _pinnedOutputBuffer;

    /// <summary>
    /// Creates a new adaptive sample rate converter.
    /// </summary>
    /// <param name="channels">Number of audio channels (1 for mono, 2 for stereo).</param>
    /// <param name="quality">Converter quality (default: SincMediumQuality for good balance).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="InvalidOperationException">If libsamplerate fails to initialize.</exception>
    public AdaptiveSampleRateConverter(
        int channels,
        int quality = SampleRateInterop.ConverterType.SincMediumQuality,
        ILogger? logger = null)
    {
        if (channels < 1 || channels > 8)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be between 1 and 8");

        _channels = channels;
        _logger = logger;

        _state = SampleRateInterop.New(quality, channels, out int error);
        if (_state == IntPtr.Zero)
        {
            var errorMsg = SampleRateInterop.GetErrorString(error);
            throw new InvalidOperationException($"Failed to create libsamplerate converter: {errorMsg}");
        }

        _logger?.LogDebug(
            "Created adaptive resampler: {Channels} channels, quality={Quality} ({QualityName})",
            channels, quality, SampleRateInterop.GetConverterName(quality));
    }

    /// <summary>
    /// Current resampling ratio (after smoothing). 1.0 = no change.
    /// </summary>
    public double CurrentRatio => _currentRatio;

    /// <summary>
    /// Target resampling ratio (before smoothing). 1.0 = no change.
    /// </summary>
    public double TargetRatio => _targetRatio;

    /// <summary>
    /// Last sync error that was used to update the ratio, in microseconds.
    /// </summary>
    public long LastSyncErrorMicroseconds => _lastSyncErrorUs;

    /// <summary>
    /// Number of times Process has been called.
    /// </summary>
    public long ProcessCallCount => _processCallCount;

    /// <summary>
    /// Current drift rate from Kalman filter in ppm.
    /// </summary>
    public double DriftRatePpm => _driftRatePpm;

    /// <summary>
    /// Whether the drift rate estimate is reliable (Kalman filter converged).
    /// </summary>
    public bool IsDriftReliable => _isDriftReliable;

    /// <summary>
    /// Updates the drift rate from the Kalman filter.
    /// Call this each time sync error is updated to provide the stable drift estimate.
    /// </summary>
    /// <param name="driftPpm">Drift rate in parts per million from ClockSyncStatus.DriftMicrosecondsPerSecond.</param>
    /// <param name="isReliable">Whether the drift estimate is reliable (from ClockSyncStatus.IsDriftReliable).</param>
    public void UpdateDriftRate(double driftPpm, bool isReliable)
    {
        _driftRatePpm = driftPpm;
        _isDriftReliable = isReliable;
    }

    /// <summary>
    /// Updates the resampling ratio using two-component control:
    /// 1. Drift term from Kalman filter (stable, immediate)
    /// 2. Offset term from sync error (very slow correction)
    /// </summary>
    /// <param name="syncErrorMicroseconds">
    /// Current sync error in microseconds.
    /// Positive = behind schedule (need to speed up).
    /// Negative = ahead of schedule (need to slow down).
    /// </param>
    public void UpdateSyncError(long syncErrorMicroseconds)
    {
        _lastSyncErrorUs = syncErrorMicroseconds;

        // === Component 1: Drift term from Kalman filter ===
        // This is the stable signal we actually want to use.
        // If drift = +50 ppm, we need ratio < 1.0 to speed up and compensate.
        // libsamplerate: ratio < 1.0 = consume MORE input = speed up playback
        double driftTerm = 0;
        if (_isDriftReliable)
        {
            // Convert ppm to ratio: -drift because we need to counter the drift
            // If server clock is faster (positive drift), we're falling behind, need to speed up
            driftTerm = -_driftRatePpm / 1_000_000.0;
        }

        // === Component 2: Offset term for residual sync error ===
        // Very slow correction (60s) to trim any accumulated offset.
        // Large deadband (30ms) to ignore jitter - only correct persistent errors.
        double offsetTerm = 0;
        if (Math.Abs(syncErrorMicroseconds) > OffsetDeadbandMicroseconds)
        {
            // Slowly correct the offset over OffsetCorrectionTimeSeconds
            // Behind schedule (positive error): need to speed up = negative ratio term
            var syncErrorSeconds = syncErrorMicroseconds / 1_000_000.0;
            offsetTerm = -syncErrorSeconds / OffsetCorrectionTimeSeconds;
        }

        // === Combined ratio ===
        _targetRatio = 1.0 + driftTerm + offsetTerm;

        // Use higher limit during fast acquisition mode (first ~10 seconds)
        // This allows faster initial sync without affecting steady-state stability
        var maxDeviation = _processCallCount < FastAcquisitionCalls
            ? FastAcquisitionMaxRatio
            : MaxRatioDeviation;

        // Clamp to maximum deviation
        _targetRatio = Math.Clamp(_targetRatio, 1.0 - maxDeviation, 1.0 + maxDeviation);

        // Smooth transition using exponential moving average (low-pass filter)
        // This prevents sudden ratio changes that could cause pitch wobble
        _currentRatio += RatioSmoothingFactor * (_targetRatio - _currentRatio);
    }

    /// <summary>
    /// Resamples audio data using the current adaptive ratio.
    /// </summary>
    /// <param name="input">Input audio samples (interleaved if multi-channel).</param>
    /// <param name="output">Output buffer for resampled audio.</param>
    /// <param name="inputFramesUsed">Number of input frames consumed.</param>
    /// <returns>Number of output frames generated.</returns>
    public int Process(ReadOnlySpan<float> input, Span<float> output, out int inputFramesUsed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _processCallCount++;

        var inputFrames = input.Length / _channels;
        var outputFrames = output.Length / _channels;

        if (inputFrames == 0 || outputFrames == 0)
        {
            inputFramesUsed = 0;
            return 0;
        }

        // Ensure we have pinned buffers of sufficient size
        EnsurePinnedBuffers(input.Length, output.Length);

        // Copy input to pinned buffer
        input.CopyTo(_pinnedInputBuffer.AsSpan());

        // Set up the SRC_DATA structure
        var srcData = new SampleRateInterop.SrcData
        {
            DataIn = _inputHandle.AddrOfPinnedObject(),
            DataOut = _outputHandle.AddrOfPinnedObject(),
            InputFrames = inputFrames,
            OutputFrames = outputFrames,
            EndOfInput = 0,
            SrcRatio = _currentRatio
        };

        // Process through libsamplerate
        var error = SampleRateInterop.Process(_state, ref srcData);
        if (error != SampleRateInterop.ErrorCode.NoError)
        {
            var errorMsg = SampleRateInterop.GetErrorString(error);
            _logger?.LogWarning("libsamplerate error: {Error}", errorMsg);
            inputFramesUsed = 0;
            return 0;
        }

        // Copy output from pinned buffer
        // Cast from nint to int - frame counts will never exceed int range for audio
        var outputSamples = (int)srcData.OutputFramesGen * _channels;
        _pinnedOutputBuffer.AsSpan(0, outputSamples).CopyTo(output);

        inputFramesUsed = (int)srcData.InputFramesUsed;
        return (int)srcData.OutputFramesGen;
    }

    /// <summary>
    /// Resets the converter state. Call when starting a new audio stream.
    /// </summary>
    /// <param name="preserveDrift">
    /// If true, keeps the drift rate from the Kalman filter for faster re-lock after reanchoring.
    /// If false (default), clears all state including drift information.
    /// </param>
    public void Reset(bool preserveDrift = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SampleRateInterop.Reset(_state);
        _currentRatio = 1.0;
        _targetRatio = 1.0;
        _lastSyncErrorUs = 0;
        _processCallCount = 0;  // Reset to re-enable fast acquisition mode

        if (!preserveDrift)
        {
            _driftRatePpm = 0;
            _isDriftReliable = false;
        }
        // Else: keep drift rate for faster re-lock after reanchoring

        _logger?.LogDebug(
            "Adaptive resampler reset (preserveDrift={PreserveDrift}, driftPpm={DriftPpm:F1})",
            preserveDrift, _driftRatePpm);
    }

    private void EnsurePinnedBuffers(int inputSize, int outputSize)
    {
        // Reallocate input buffer if needed
        if (_pinnedInputBuffer == null || _pinnedInputBuffer.Length < inputSize)
        {
            FreePinnedBuffer(ref _inputHandle, ref _pinnedInputBuffer);
            _pinnedInputBuffer = new float[inputSize];
            _inputHandle = GCHandle.Alloc(_pinnedInputBuffer, GCHandleType.Pinned);
        }

        // Reallocate output buffer if needed
        if (_pinnedOutputBuffer == null || _pinnedOutputBuffer.Length < outputSize)
        {
            FreePinnedBuffer(ref _outputHandle, ref _pinnedOutputBuffer);
            _pinnedOutputBuffer = new float[outputSize];
            _outputHandle = GCHandle.Alloc(_pinnedOutputBuffer, GCHandleType.Pinned);
        }
    }

    private static void FreePinnedBuffer(ref GCHandle handle, ref float[]? buffer)
    {
        if (handle.IsAllocated)
        {
            handle.Free();
        }
        buffer = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        FreePinnedBuffer(ref _inputHandle, ref _pinnedInputBuffer);
        FreePinnedBuffer(ref _outputHandle, ref _pinnedOutputBuffer);

        if (_state != IntPtr.Zero)
        {
            SampleRateInterop.Delete(_state);
        }

        _logger?.LogDebug("Adaptive resampler disposed after {Calls} process calls", _processCallCount);
    }
}
