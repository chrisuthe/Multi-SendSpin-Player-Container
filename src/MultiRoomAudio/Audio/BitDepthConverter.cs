namespace MultiRoomAudio.Audio;

/// <summary>
/// Converts 32-bit float audio samples to integer formats for output.
/// Supports 16-bit, 24-bit (packed and padded), and 32-bit integer output.
/// </summary>
public static class BitDepthConverter
{
    // Thread-local random generator for TPDF dithering (avoids lock contention in hot path)
    [ThreadStatic]
    private static Random? t_ditherRandom;
    private static Random DitherRandom => t_ditherRandom ??= new Random();

    /// <summary>
    /// Converts float samples (-1.0 to 1.0) to signed 16-bit little-endian with optional dithering.
    /// </summary>
    /// <param name="input">Input float samples.</param>
    /// <param name="output">Output byte buffer (must be 2x input length).</param>
    /// <param name="applyDither">Apply TPDF dithering to reduce quantization noise.</param>
    public static void FloatToS16(ReadOnlySpan<float> input, Span<byte> output, bool applyDither = true)
    {
        const float scale = 32767f;
        const float ditherScale = 1f / 32768f;

        for (int i = 0; i < input.Length; i++)
        {
            var sample = input[i];

            // Apply TPDF dithering before quantization
            if (applyDither)
            {
                var dither = ((float)DitherRandom.NextDouble() - 0.5f) * 2f * ditherScale;
                sample += dither;
            }

            // Scale, clamp, and round (rounding reduces quantization distortion and DC bias)
            var scaled = Math.Clamp(sample * scale, -32768f, 32767f);
            var intSample = (short)Math.Round(scaled);

            // Write little-endian
            var outIdx = i * 2;
            output[outIdx] = (byte)(intSample & 0xFF);
            output[outIdx + 1] = (byte)((intSample >> 8) & 0xFF);
        }
    }

    /// <summary>
    /// Converts float samples to signed 24-bit little-endian in 4-byte containers (S24_LE format).
    /// </summary>
    /// <param name="input">Input float samples.</param>
    /// <param name="output">Output byte buffer (must be 4x input length).</param>
    public static void FloatToS24(ReadOnlySpan<float> input, Span<byte> output)
    {
        const float scale = 8388607f; // 2^23 - 1

        for (int i = 0; i < input.Length; i++)
        {
            var sample = input[i];
            var scaled = Math.Clamp(sample * scale, -8388608f, 8388607f);
            var intSample = (int)Math.Round(scaled);

            // Write 24-bit in 4-byte container, little-endian
            var outIdx = i * 4;
            output[outIdx] = (byte)(intSample & 0xFF);
            output[outIdx + 1] = (byte)((intSample >> 8) & 0xFF);
            output[outIdx + 2] = (byte)((intSample >> 16) & 0xFF);
            output[outIdx + 3] = 0; // Padding byte
        }
    }

    /// <summary>
    /// Converts float samples to signed 24-bit packed little-endian (S24_3LE format).
    /// Uses 3 bytes per sample with no padding.
    /// </summary>
    /// <param name="input">Input float samples.</param>
    /// <param name="output">Output byte buffer (must be 3x input length).</param>
    public static void FloatToS24Packed(ReadOnlySpan<float> input, Span<byte> output)
    {
        const float scale = 8388607f; // 2^23 - 1

        for (int i = 0; i < input.Length; i++)
        {
            var sample = input[i];
            var scaled = Math.Clamp(sample * scale, -8388608f, 8388607f);
            var intSample = (int)Math.Round(scaled);

            // Write 24-bit packed, little-endian (3 bytes)
            var outIdx = i * 3;
            output[outIdx] = (byte)(intSample & 0xFF);
            output[outIdx + 1] = (byte)((intSample >> 8) & 0xFF);
            output[outIdx + 2] = (byte)((intSample >> 16) & 0xFF);
        }
    }

    /// <summary>
    /// Converts float samples to signed 32-bit little-endian.
    /// </summary>
    /// <param name="input">Input float samples.</param>
    /// <param name="output">Output byte buffer (must be 4x input length).</param>
    public static void FloatToS32(ReadOnlySpan<float> input, Span<byte> output)
    {
        const double scale = 2147483647.0; // 2^31 - 1

        for (int i = 0; i < input.Length; i++)
        {
            var sample = (double)input[i];
            var scaled = Math.Clamp(sample * scale, -2147483648.0, 2147483647.0);
            var intSample = (int)Math.Round(scaled);

            // Write little-endian
            var outIdx = i * 4;
            output[outIdx] = (byte)(intSample & 0xFF);
            output[outIdx + 1] = (byte)((intSample >> 8) & 0xFF);
            output[outIdx + 2] = (byte)((intSample >> 16) & 0xFF);
            output[outIdx + 3] = (byte)((intSample >> 24) & 0xFF);
        }
    }

    /// <summary>
    /// Gets the number of bytes per sample for a given bit depth.
    /// </summary>
    /// <param name="bitDepth">Bit depth (16, 24, or 32).</param>
    /// <param name="packed">For 24-bit, whether to use packed 3-byte format.</param>
    public static int GetBytesPerSample(int bitDepth, bool packed = false)
    {
        return bitDepth switch
        {
            16 => 2,
            24 => packed ? 3 : 4,
            32 => 4,
            _ => 4 // Default to 32-bit
        };
    }

    /// <summary>
    /// Converts float samples to the specified bit depth.
    /// </summary>
    /// <param name="input">Input float samples.</param>
    /// <param name="output">Output byte buffer.</param>
    /// <param name="bitDepth">Target bit depth (16, 24, or 32).</param>
    /// <param name="packed">For 24-bit, whether to use packed 3-byte format.</param>
    /// <param name="applyDither">Apply dithering for 16-bit output.</param>
    public static void Convert(
        ReadOnlySpan<float> input,
        Span<byte> output,
        int bitDepth,
        bool packed = false,
        bool applyDither = true)
    {
        switch (bitDepth)
        {
            case 16:
                FloatToS16(input, output, applyDither);
                break;
            case 24:
                if (packed)
                    FloatToS24Packed(input, output);
                else
                    FloatToS24(input, output);
                break;
            case 32:
                FloatToS32(input, output);
                break;
            default:
                // For unknown bit depths, use 32-bit
                FloatToS32(input, output);
                break;
        }
    }
}
