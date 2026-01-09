using System.Diagnostics;

namespace MultiRoomAudio.Services;

/// <summary>
/// Service for generating and playing test tones through specific audio sinks.
/// Used for device identification during onboarding.
/// </summary>
public class ToneGeneratorService
{
    private readonly ILogger<ToneGeneratorService> _logger;

    // Default tone parameters
    private const int DefaultFrequency = 1000;  // 1kHz sine wave
    private const int DefaultDuration = 1500;   // 1.5 seconds
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const int BitsPerSample = 16;

    // Track active playback to prevent overlapping tones
    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    public ToneGeneratorService(ILogger<ToneGeneratorService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Play a test tone through a specific PulseAudio sink.
    /// </summary>
    /// <param name="sinkName">PulseAudio sink name (device ID)</param>
    /// <param name="frequencyHz">Tone frequency in Hz (default: 1000)</param>
    /// <param name="durationMs">Duration in milliseconds (default: 1500)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task PlayTestToneAsync(
        string sinkName,
        int frequencyHz = DefaultFrequency,
        int durationMs = DefaultDuration,
        CancellationToken ct = default)
    {
        // Prevent overlapping playback
        if (!await _playbackLock.WaitAsync(0, ct))
        {
            _logger.LogDebug("Test tone already playing, skipping request");
            throw new InvalidOperationException("A test tone is already playing");
        }

        string? tempFile = null;
        try
        {
            _logger.LogInformation("Playing test tone: {Frequency}Hz for {Duration}ms on sink {Sink}",
                frequencyHz, durationMs, sinkName);

            // Generate WAV file
            var wavData = GenerateWavFile(frequencyHz, durationMs);

            // Write to temp file
            tempFile = Path.Combine(Path.GetTempPath(), $"testtone_{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(tempFile, wavData, ct);
            _logger.LogDebug("Generated test tone WAV file: {TempFile} ({Size} bytes)", tempFile, wavData.Length);

            // Play via paplay
            await PlayWithPaplayAsync(sinkName, tempFile, ct);

            _logger.LogDebug("Test tone playback completed");
        }
        finally
        {
            _playbackLock.Release();

            // Clean up temp file
            if (tempFile != null && File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                    _logger.LogDebug("Cleaned up temp file: {TempFile}", tempFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temp file: {TempFile}", tempFile);
                }
            }
        }
    }

    /// <summary>
    /// Generate a WAV file containing a sine wave tone.
    /// </summary>
    private byte[] GenerateWavFile(int frequencyHz, int durationMs)
    {
        var numSamples = (int)(SampleRate * durationMs / 1000.0);
        var dataSize = numSamples * Channels * (BitsPerSample / 8);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // WAV header
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);  // File size - 8
        writer.Write("WAVE"u8);

        // Format chunk
        writer.Write("fmt "u8);
        writer.Write(16);  // Chunk size
        writer.Write((short)1);  // PCM format
        writer.Write((short)Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * BitsPerSample / 8);  // Byte rate
        writer.Write((short)(Channels * BitsPerSample / 8));  // Block align
        writer.Write((short)BitsPerSample);

        // Data chunk
        writer.Write("data"u8);
        writer.Write(dataSize);

        // Generate sine wave samples with fade in/out to avoid clicks
        var fadeLength = Math.Min(numSamples / 10, SampleRate / 20);  // 50ms fade or 10% of duration

        for (int i = 0; i < numSamples; i++)
        {
            // Calculate sine wave sample
            double t = (double)i / SampleRate;
            double sample = Math.Sin(2 * Math.PI * frequencyHz * t);

            // Apply fade envelope
            double envelope = 1.0;
            if (i < fadeLength)
            {
                envelope = (double)i / fadeLength;  // Fade in
            }
            else if (i > numSamples - fadeLength)
            {
                envelope = (double)(numSamples - i) / fadeLength;  // Fade out
            }

            sample *= envelope;

            // Convert to 16-bit signed integer
            short sampleInt = (short)(sample * short.MaxValue * 0.8);  // 80% volume to avoid clipping

            // Write sample for each channel (stereo)
            for (int ch = 0; ch < Channels; ch++)
            {
                writer.Write(sampleInt);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Play a WAV file through paplay to a specific sink.
    /// </summary>
    private async Task PlayWithPaplayAsync(string sinkName, string wavFile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "paplay",
            ArgumentList = { $"--device={sinkName}", wavFile },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogDebug("Running: paplay --device={Sink} {File}", sinkName, wavFile);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Wait for completion with timeout
        var timeoutMs = 10000;  // 10 second timeout
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("paplay timed out after {Timeout}ms, killing process", timeoutMs);
            process.Kill();
            throw new TimeoutException($"Test tone playback timed out after {timeoutMs}ms");
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            _logger.LogWarning("paplay exited with code {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"paplay failed: {error}");
        }
    }

    /// <summary>
    /// Check if paplay is available on the system.
    /// </summary>
    public async Task<bool> IsPaplayAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "paplay",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
