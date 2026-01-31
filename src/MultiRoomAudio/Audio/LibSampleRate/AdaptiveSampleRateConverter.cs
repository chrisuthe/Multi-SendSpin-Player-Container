using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MultiRoomAudio.Audio.LibSampleRate;

/// <summary>
/// Managed wrapper for libsamplerate with adaptive ratio control for clock drift compensation.
/// Implements a PLL-like control loop that continuously adjusts the resampling ratio
/// to maintain synchronization, spreading corrections across every sample for inaudible adjustment.
/// </summary>
/// <remarks>
/// Thread-safety: This class is designed for single producer/consumer pattern.
/// UpdateSyncError and Process should be called from the same thread (audio callback thread).
/// </remarks>
public sealed class AdaptiveSampleRateConverter : IDisposable
{
    private readonly IntPtr _state;
    private readonly int _channels;
    private readonly ILogger? _logger;
    private bool _disposed;

    // PLL-like control loop parameters
    // These are tuned for audio synchronization where we want smooth, gradual corrections.

    /// <summary>
    /// Time constant for error correction in seconds.
    /// Higher values = smoother corrections but slower convergence.
    /// 10.0 seconds provides stability on jittery systems like PulseAudio.
    /// </summary>
    private const double CorrectionTimeSeconds = 10.0;

    /// <summary>
    /// Maximum deviation from 1.0 ratio (±0.5% = 5,000 ppm).
    /// This limits how fast we can speed up or slow down.
    /// Real clock drift is typically 1-100 ppm, so 0.5% is generous.
    /// </summary>
    private const double MaxRatioDeviation = 0.005;

    /// <summary>
    /// Low-pass filter coefficient for smoothing ratio changes.
    /// Lower values provide stronger damping to prevent oscillation.
    /// 0.02 takes ~50 calls (~1s) to converge, providing good stability.
    /// </summary>
    private const double RatioSmoothingFactor = 0.02;

    /// <summary>
    /// Deadband in microseconds - don't adjust ratio for very small errors.
    /// This prevents over-correction for measurement noise.
    /// 5000us = 5ms deadband - ignores typical PulseAudio jitter (1-3ms).
    /// </summary>
    private const long DeadbandMicroseconds = 5000;

    // Current state
    private double _currentRatio = 1.0;
    private double _targetRatio = 1.0;
    private long _lastSyncErrorUs;
    private long _processCallCount;

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
    /// Updates the resampling ratio based on current sync error.
    /// Call this periodically (typically every audio callback, ~20ms).
    /// </summary>
    /// <param name="syncErrorMicroseconds">
    /// Current sync error in microseconds.
    /// Positive = behind schedule (need to speed up, ratio > 1.0).
    /// Negative = ahead of schedule (need to slow down, ratio &lt; 1.0).
    /// </param>
    public void UpdateSyncError(long syncErrorMicroseconds)
    {
        _lastSyncErrorUs = syncErrorMicroseconds;

        // Apply deadband - don't adjust for very small errors
        if (Math.Abs(syncErrorMicroseconds) < DeadbandMicroseconds)
        {
            // Gradually return to 1.0 when within deadband
            _targetRatio = 1.0;
        }
        else
        {
            // Calculate target ratio to eliminate error over CorrectionTimeSeconds
            //
            // libsamplerate ratio = output_samples / input_samples
            // - ratio > 1.0: more output per input = consume FEWER input samples = slow down
            // - ratio < 1.0: fewer output per input = consume MORE input samples = speed up
            //
            // So we SUBTRACT the error to get the correct direction:
            // - Behind schedule (positive error): need to speed up = ratio < 1.0
            // - Ahead of schedule (negative error): need to slow down = ratio > 1.0
            //
            // Example: If 10ms behind (error = +10,000us):
            //   error_seconds = 0.01
            //   ratio = 1.0 - (0.01 / 2.0) = 0.995
            //   This consumes input 0.5% faster, eliminating 10ms error in 2 seconds.
            var syncErrorSeconds = syncErrorMicroseconds / 1_000_000.0;
            _targetRatio = 1.0 - (syncErrorSeconds / CorrectionTimeSeconds);
        }

        // Clamp to maximum deviation
        _targetRatio = Math.Clamp(_targetRatio, 1.0 - MaxRatioDeviation, 1.0 + MaxRatioDeviation);

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
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SampleRateInterop.Reset(_state);
        _currentRatio = 1.0;
        _targetRatio = 1.0;
        _lastSyncErrorUs = 0;
        _processCallCount = 0;

        _logger?.LogDebug("Adaptive resampler reset");
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
