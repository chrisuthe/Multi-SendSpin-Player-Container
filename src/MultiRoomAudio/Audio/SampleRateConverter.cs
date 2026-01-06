using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// High-quality sample rate converter using windowed-sinc interpolation.
/// Converts audio from one sample rate to another (e.g., 48kHz to 192kHz).
/// </summary>
/// <remarks>
/// This converter uses a polyphase implementation of windowed-sinc interpolation
/// with a Kaiser window. It's optimized for common upsampling ratios (2x, 4x).
///
/// For integer ratio upsampling:
/// 1. Zero-stuffing: Insert (ratio-1) zero samples between each input sample
/// 2. Low-pass filter: Apply windowed-sinc FIR filter to remove imaging artifacts
/// 3. Polyphase optimization: Compute only needed output samples
/// </remarks>
public sealed class SampleRateConverter : IAudioSampleSource, IDisposable
{
    private readonly IAudioSampleSource _source;
    private readonly ILogger? _logger;
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly int _channels;
    private readonly double _ratio;
    private readonly AudioFormat _outputFormat;

    // Filter parameters
    private readonly int _filterTaps;
    private readonly double[] _filterCoefficients;

    // Polyphase filter bank (for integer ratios)
    private readonly int _polyphaseCount;
    private readonly double[][] _polyphaseFilters;

    // Internal buffers
    private readonly float[] _inputBuffer;
    private readonly float[] _historyBuffer;
    private int _historyLength;
    private double _fractionalPosition;
    private bool _disposed;

    // Constants
    private const int DefaultFilterTaps = 32;
    private const double KaiserBeta = 6.0;

    /// <inheritdoc/>
    public AudioFormat Format => _outputFormat;

    /// <summary>
    /// Gets the input sample rate.
    /// </summary>
    public int InputRate => _inputRate;

    /// <summary>
    /// Gets the output sample rate.
    /// </summary>
    public int OutputRate => _outputRate;

    /// <summary>
    /// Creates a new sample rate converter.
    /// </summary>
    /// <param name="source">Source audio sample source.</param>
    /// <param name="inputRate">Input sample rate in Hz.</param>
    /// <param name="outputRate">Output sample rate in Hz.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="filterTaps">Number of filter taps (default 32, higher = better quality but more CPU).</param>
    public SampleRateConverter(
        IAudioSampleSource source,
        int inputRate,
        int outputRate,
        ILogger? logger = null,
        int filterTaps = DefaultFilterTaps)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _logger = logger;
        _inputRate = inputRate;
        _outputRate = outputRate;
        _channels = source.Format.Channels;
        _ratio = (double)outputRate / inputRate;
        _filterTaps = filterTaps;

        // Create output format with new sample rate
        _outputFormat = new AudioFormat
        {
            SampleRate = outputRate,
            Channels = source.Format.Channels,
            Codec = source.Format.Codec
        };

        // Calculate filter coefficients
        _filterCoefficients = DesignLowPassFilter(filterTaps, Math.Min(1.0 / _ratio, 1.0));

        // Create polyphase filter bank for integer ratios
        var gcd = Gcd(outputRate, inputRate);
        var upFactor = outputRate / gcd;
        var downFactor = inputRate / gcd;

        if (upFactor <= 16 && downFactor == 1)
        {
            // Integer upsampling - use polyphase decomposition
            _polyphaseCount = upFactor;
            _polyphaseFilters = CreatePolyphaseBank(_filterCoefficients, upFactor);
            _logger?.LogDebug("Created {Count}x polyphase filter bank for {In}Hz -> {Out}Hz",
                upFactor, inputRate, outputRate);
        }
        else
        {
            // Arbitrary ratio - use direct sinc interpolation
            _polyphaseCount = 0;
            _polyphaseFilters = Array.Empty<double[]>();
            _logger?.LogDebug("Using arbitrary ratio resampling for {In}Hz -> {Out}Hz (ratio={Ratio:F4})",
                inputRate, outputRate, _ratio);
        }

        // Allocate buffers
        var maxInputNeeded = (int)Math.Ceiling(8192 / _ratio) + filterTaps;
        _inputBuffer = new float[maxInputNeeded * _channels];
        _historyBuffer = new float[filterTaps * _channels];
        _historyLength = 0;
        _fractionalPosition = 0;

        _logger?.LogInformation(
            "Sample rate converter: {InputRate}Hz -> {OutputRate}Hz ({Ratio:F2}x), {Taps} taps",
            inputRate, outputRate, _ratio, filterTaps);
    }

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed)
            return 0;

        var outputFrames = count / _channels;
        var inputFramesNeeded = (int)Math.Ceiling(outputFrames / _ratio) + _filterTaps;

        // Read input samples
        var inputSamples = inputFramesNeeded * _channels;
        if (inputSamples > _inputBuffer.Length)
            inputSamples = _inputBuffer.Length;

        var samplesRead = _source.Read(_inputBuffer, 0, inputSamples);
        var inputFramesRead = samplesRead / _channels;

        if (inputFramesRead == 0)
        {
            // Fill with silence
            Array.Fill(buffer, 0f, offset, count);
            return count;
        }

        // Perform resampling
        int outputSamplesWritten;
        if (_polyphaseCount > 0)
        {
            outputSamplesWritten = ResamplePolyphase(
                _inputBuffer, inputFramesRead,
                buffer, offset, outputFrames);
        }
        else
        {
            outputSamplesWritten = ResampleArbitrary(
                _inputBuffer, inputFramesRead,
                buffer, offset, outputFrames);
        }

        // Fill remaining with silence if needed
        if (outputSamplesWritten < count)
        {
            Array.Fill(buffer, 0f, offset + outputSamplesWritten, count - outputSamplesWritten);
        }

        return count;
    }

    private int ResamplePolyphase(float[] input, int inputFrames, float[] output, int outputOffset, int maxOutputFrames)
    {
        var outputSamples = 0;
        var inputIdx = 0;
        var phaseIdx = (int)(_fractionalPosition * _polyphaseCount);

        while (outputSamples < maxOutputFrames * _channels && inputIdx < inputFrames)
        {
            // Get the appropriate polyphase filter
            var filter = _polyphaseFilters[phaseIdx % _polyphaseCount];
            var filterLen = filter.Length;

            // Compute output sample for each channel
            for (int ch = 0; ch < _channels; ch++)
            {
                double sum = 0;
                for (int t = 0; t < filterLen; t++)
                {
                    var idx = inputIdx - filterLen / 2 + t;
                    if (idx >= 0 && idx < inputFrames)
                    {
                        sum += input[idx * _channels + ch] * filter[t];
                    }
                    else if (idx < 0 && _historyLength > 0)
                    {
                        // histIdx is guaranteed < _historyLength because idx < 0 (entry condition)
                        // Only need to check lower bound (histIdx >= 0) for valid history access
                        var histIdx = _historyLength + idx;
                        if (histIdx >= 0)
                        {
                            sum += _historyBuffer[histIdx * _channels + ch] * filter[t];
                        }
                    }
                }
                output[outputOffset + outputSamples + ch] = (float)sum;
            }

            outputSamples += _channels;

            // Advance through polyphase filters
            phaseIdx++;
            if (phaseIdx >= _polyphaseCount)
            {
                phaseIdx = 0;
                inputIdx++;
            }
        }

        // Update history for next call
        UpdateHistory(input, inputFrames);

        // Track fractional position
        _fractionalPosition = (double)phaseIdx / _polyphaseCount;

        return outputSamples;
    }

    private int ResampleArbitrary(float[] input, int inputFrames, float[] output, int outputOffset, int maxOutputFrames)
    {
        var outputSamples = 0;
        var pos = _fractionalPosition;
        var step = 1.0 / _ratio;
        var halfTaps = _filterTaps / 2;

        while (outputSamples < maxOutputFrames * _channels && pos < inputFrames - halfTaps)
        {
            var intPos = (int)pos;
            var frac = pos - intPos;

            for (int ch = 0; ch < _channels; ch++)
            {
                double sum = 0;
                for (int t = -halfTaps; t < halfTaps; t++)
                {
                    var idx = intPos + t;
                    if (idx >= 0 && idx < inputFrames)
                    {
                        var x = t - frac;
                        var sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                        var window = Kaiser(t + halfTaps, _filterTaps, KaiserBeta);
                        sum += input[idx * _channels + ch] * sinc * window;
                    }
                }
                output[outputOffset + outputSamples + ch] = (float)sum;
            }

            outputSamples += _channels;
            pos += step;
        }

        // Update history and fractional position
        UpdateHistory(input, inputFrames);
        _fractionalPosition = pos - inputFrames + halfTaps;
        if (_fractionalPosition < 0) _fractionalPosition = 0;

        return outputSamples;
    }

    private void UpdateHistory(float[] input, int inputFrames)
    {
        var historyFrames = Math.Min(_filterTaps, inputFrames);
        var startFrame = inputFrames - historyFrames;

        Array.Copy(input, startFrame * _channels, _historyBuffer, 0, historyFrames * _channels);
        _historyLength = historyFrames;
    }

    private static double[] DesignLowPassFilter(int taps, double cutoff)
    {
        var filter = new double[taps];
        var halfTaps = taps / 2;
        double sum = 0;

        for (int i = 0; i < taps; i++)
        {
            var x = i - halfTaps;
            var sinc = x == 0 ? cutoff : Math.Sin(Math.PI * cutoff * x) / (Math.PI * x);
            var window = Kaiser(i, taps, KaiserBeta);
            filter[i] = sinc * window;
            sum += filter[i];
        }

        // Normalize
        for (int i = 0; i < taps; i++)
        {
            filter[i] /= sum;
        }

        return filter;
    }

    private static double[][] CreatePolyphaseBank(double[] filter, int phases)
    {
        var filterLen = filter.Length;
        var phaseLen = (filterLen + phases - 1) / phases;
        var bank = new double[phases][];

        for (int p = 0; p < phases; p++)
        {
            bank[p] = new double[phaseLen];
            for (int i = 0; i < phaseLen; i++)
            {
                var idx = i * phases + p;
                bank[p][i] = idx < filterLen ? filter[idx] * phases : 0;
            }
        }

        return bank;
    }

    private static double Kaiser(int n, int length, double beta)
    {
        var alpha = (length - 1) / 2.0;
        var x = (n - alpha) / alpha;
        var arg = beta * Math.Sqrt(1.0 - x * x);
        return BesselI0(arg) / BesselI0(beta);
    }

    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        var halfX = x / 2;

        for (int k = 1; k < 25; k++)
        {
            term *= (halfX / k) * (halfX / k);
            sum += term;
            if (term < 1e-10 * sum)
                break;
        }

        return sum;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            var t = b;
            b = a % b;
            a = t;
        }
        return a;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
