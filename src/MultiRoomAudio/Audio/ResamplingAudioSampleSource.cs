using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Wraps an <see cref="IAudioSampleSource"/> with dynamic playback rate adjustment.
/// Subscribes to <see cref="ITimedAudioBuffer.TargetPlaybackRateChanged"/> for smooth sync correction.
/// </summary>
/// <remarks>
/// Uses linear interpolation for resampling, which is sufficient for the ±4% rate range
/// used by the SDK's tiered sync correction. This avoids audible clicks that occur with
/// discrete sample dropping/insertion.
///
/// Rate interpretation:
/// - 1.0 = normal playback (bypass resampling)
/// - >1.0 = speed up (we're behind, consume more source samples)
/// - &lt;1.0 = slow down (we're ahead, consume fewer source samples)
/// </remarks>
public sealed class ResamplingAudioSampleSource : IAudioSampleSource
{
    private readonly IAudioSampleSource _source;
    private readonly ITimedAudioBuffer? _buffer;

    // Playback rate (1.0 = normal, clamped to 0.96-1.04)
    private double _playbackRate = 1.0;
    private double _lastRate = 1.0; // Track previous rate for transition detection
    private const double MinRate = 0.96;
    private const double MaxRate = 1.04;

    // Resampling state
    private float[] _sourceBuffer = Array.Empty<float>();
    private int _sourceBufferValidSamples;
    private int _sourceBufferPosition;
    private double _fractionalPosition; // Sub-sample position for interpolation
    private int _channels;

    /// <inheritdoc/>
    public AudioFormat Format => _source.Format;

    /// <summary>
    /// Gets or sets the current playback rate.
    /// Values are clamped to [0.96, 1.04] range.
    /// </summary>
    public double PlaybackRate
    {
        get => _playbackRate;
        set => _playbackRate = Math.Clamp(value, MinRate, MaxRate);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResamplingAudioSampleSource"/> class.
    /// </summary>
    /// <param name="source">The underlying audio sample source.</param>
    /// <param name="buffer">Optional timed buffer to subscribe to rate changes. If null, resampling is disabled.</param>
    public ResamplingAudioSampleSource(IAudioSampleSource source, ITimedAudioBuffer? buffer = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _buffer = buffer;
        _channels = source.Format.Channels;

        // Pre-allocate source buffer for resampling (enough for ~4096 frames at max rate)
        _sourceBuffer = new float[8192 * _channels];

        // Subscribe to rate changes from the SDK's sync correction
        if (_buffer != null)
        {
            _buffer.TargetPlaybackRateChanged += OnTargetPlaybackRateChanged;
        }
    }

    private void OnTargetPlaybackRateChanged(double newRate)
    {
        PlaybackRate = newRate;
    }

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
        var currentRate = _playbackRate;
        var wasInBypass = Math.Abs(_lastRate - 1.0) < 0.0001;
        var isInBypass = Math.Abs(currentRate - 1.0) < 0.0001;

        // Detect transition from bypass to resampling - reset fractional position
        // to avoid discontinuities from stale interpolation state
        if (wasInBypass && !isInBypass)
        {
            _fractionalPosition = 0;
        }

        _lastRate = currentRate;

        // Fast path: if rate is essentially 1.0, pass through directly
        if (isInBypass)
        {
            return _source.Read(buffer, offset, count);
        }

        // Resampling path
        return ReadWithResampling(buffer, offset, count);
    }

    private int ReadWithResampling(float[] buffer, int offset, int count)
    {
        var framesNeeded = count / _channels;
        var framesWritten = 0;
        var outputIndex = offset;

        while (framesWritten < framesNeeded)
        {
            // Ensure we have source samples to interpolate from
            if (!EnsureSourceSamples())
            {
                // Source exhausted, fill remainder with silence
                var remaining = (framesNeeded - framesWritten) * _channels;
                Array.Fill(buffer, 0f, outputIndex, remaining);
                return count;
            }

            // Calculate how many output frames we can produce from current source buffer
            var sourceFramesRemaining = (_sourceBufferValidSamples / _channels) - _sourceBufferPosition - 1;
            if (sourceFramesRemaining <= 0)
            {
                // Need more source data for interpolation
                _sourceBufferPosition = 0;
                _sourceBufferValidSamples = 0;
                continue;
            }

            // Produce output frames using linear interpolation
            while (framesWritten < framesNeeded && _sourceBufferPosition < (_sourceBufferValidSamples / _channels) - 1)
            {
                var sourceFrame = _sourceBufferPosition;
                var frac = (float)_fractionalPosition;

                // Linear interpolate each channel
                for (int ch = 0; ch < _channels; ch++)
                {
                    var idx0 = sourceFrame * _channels + ch;
                    var idx1 = (sourceFrame + 1) * _channels + ch;
                    var sample0 = _sourceBuffer[idx0];
                    var sample1 = _sourceBuffer[idx1];
                    buffer[outputIndex + ch] = sample0 + (sample1 - sample0) * frac;
                }

                outputIndex += _channels;
                framesWritten++;

                // Advance position by playback rate
                _fractionalPosition += _playbackRate;

                // When we cross into next frame, update integer position
                while (_fractionalPosition >= 1.0 && _sourceBufferPosition < (_sourceBufferValidSamples / _channels) - 1)
                {
                    _fractionalPosition -= 1.0;
                    _sourceBufferPosition++;
                }
            }
        }

        return count;
    }

    private bool EnsureSourceSamples()
    {
        // If we still have enough samples for interpolation, we're good
        if (_sourceBufferPosition < (_sourceBufferValidSamples / _channels) - 1)
        {
            return true;
        }

        // Shift remaining samples to beginning if any
        var remainingFrames = (_sourceBufferValidSamples / _channels) - _sourceBufferPosition;
        if (remainingFrames > 0 && _sourceBufferPosition > 0)
        {
            var remainingSamples = remainingFrames * _channels;
            Array.Copy(_sourceBuffer, _sourceBufferPosition * _channels, _sourceBuffer, 0, remainingSamples);
            _sourceBufferValidSamples = remainingSamples;
            _sourceBufferPosition = 0;
        }
        else
        {
            _sourceBufferValidSamples = 0;
            _sourceBufferPosition = 0;
        }

        // Read more samples from source
        // Request enough to fill our buffer, accounting for rate adjustments
        var samplesToRead = _sourceBuffer.Length - _sourceBufferValidSamples;
        if (samplesToRead <= 0)
            return true;

        var read = _source.Read(_sourceBuffer, _sourceBufferValidSamples, samplesToRead);
        _sourceBufferValidSamples += read;

        return _sourceBufferValidSamples > _channels; // Need at least 2 frames for interpolation
    }
}
