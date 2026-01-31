using MultiRoomAudio.Models;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace MultiRoomAudio.Services;

/// <summary>
/// Transforms player pipeline and clock data into PlayerStatsResponse for the Stats for Nerds UI.
/// </summary>
/// <remarks>
/// This is a pure transformation with no side effects - it extracts data from SDK objects
/// and formats it into the stats response model. Extracted from PlayerManagerService to
/// improve testability and reduce method size.
/// </remarks>
internal static class PlayerStatsMapper
{
    /// <summary>
    /// Sync error tolerance in milliseconds. Errors below this are considered "in sync".
    /// Must match CorrectionThresholdMicroseconds in BufferedAudioSampleSource.
    /// </summary>
    private const double SyncToleranceMs = 15.0;

    /// <summary>
    /// Builds a complete stats response from pipeline and clock synchronizer data.
    /// </summary>
    /// <param name="playerName">Name of the player.</param>
    /// <param name="pipeline">The audio pipeline providing buffer and format stats.</param>
    /// <param name="clockSync">The clock synchronizer providing timing stats.</param>
    /// <param name="player">The audio player providing output latency.</param>
    /// <param name="device">The audio device for hardware format info (optional).</param>
    /// <param name="resampleRatio">Current resampling ratio if using adaptive resampling (optional).</param>
    /// <returns>Complete stats response for the UI.</returns>
    public static PlayerStatsResponse BuildStats(
        string playerName,
        IAudioPipeline pipeline,
        IClockSynchronizer clockSync,
        IAudioPlayer player,
        AudioDevice? device = null,
        double? resampleRatio = null)
    {
        // Single snapshot of buffer stats — one lock acquisition instead of five.
        // This matches the Windows version's pattern of snapshotting the struct once
        // and avoids lock contention with the audio thread at high bitrates.
        var bufferStats = pipeline.BufferStats;
        var clockStatus = clockSync.GetStatus();
        var inputFormat = pipeline.CurrentFormat;
        var outputFormat = pipeline.OutputFormat ?? inputFormat;
        var pipelineState = pipeline.State.ToString();

        return new PlayerStatsResponse(
            PlayerName: playerName,
            AudioFormat: BuildAudioFormatStats(inputFormat, outputFormat, device),
            Sync: BuildSyncStats(bufferStats),
            Buffer: BuildBufferStats(bufferStats),
            ClockSync: BuildClockSyncStats(clockStatus, player, clockSync),
            Throughput: BuildThroughputStats(bufferStats),
            Correction: BuildSyncCorrectionStats(bufferStats, resampleRatio),
            Diagnostics: BuildBufferDiagnostics(bufferStats, pipelineState)
        );
    }

    /// <summary>
    /// Builds audio format statistics showing input codec, output format, and hardware sink format.
    /// </summary>
    private static AudioFormatStats BuildAudioFormatStats(
        AudioFormat? inputFormat,
        AudioFormat? outputFormat,
        AudioDevice? device)
    {
        return new AudioFormatStats(
            InputFormat: inputFormat != null
                ? $"{inputFormat.Codec.ToUpperInvariant()} {inputFormat.SampleRate}Hz {inputFormat.Channels}ch"
                : "--",
            InputSampleRate: inputFormat?.SampleRate ?? 0,
            InputChannels: inputFormat?.Channels ?? 0,
            InputBitrate: inputFormat?.Bitrate > 0 ? $"{inputFormat.Bitrate}kbps" : null,
            OutputFormat: outputFormat != null
                ? $"FLOAT32 {outputFormat.SampleRate}Hz {outputFormat.Channels}ch"
                : "--",
            OutputSampleRate: outputFormat?.SampleRate ?? 0,
            OutputChannels: outputFormat?.Channels ?? 2,
            OutputBitDepth: 32,  // Always float32 (PulseAudio converts to device format)
                                 // Hardware sink format from PulseAudio (what the DAC actually receives)
            HardwareFormat: device?.SampleFormat?.ToUpperInvariant(),
            HardwareSampleRate: device?.DefaultSampleRate,
            HardwareBitDepth: device?.BitDepth
        );
    }

    /// <summary>
    /// Builds sync statistics showing current error and tolerance status.
    /// </summary>
    private static SyncStats BuildSyncStats(AudioBufferStats? bufferStats)
    {
        var syncErrorMs = bufferStats?.SyncErrorMs ?? 0;
        return new SyncStats(
            SyncErrorMs: syncErrorMs,
            IsWithinTolerance: Math.Abs(syncErrorMs) < SyncToleranceMs,
            IsPlaybackActive: bufferStats?.IsPlaybackActive ?? false
        );
    }

    /// <summary>
    /// Builds buffer level statistics showing fill level and under/overrun counts.
    /// </summary>
    private static BufferStatsInfo BuildBufferStats(AudioBufferStats? bufferStats)
    {
        return new BufferStatsInfo(
            BufferedMs: (int)(bufferStats?.BufferedMs ?? 0),
            TargetMs: 5000,
            Underruns: bufferStats?.UnderrunCount ?? 0,
            Overruns: bufferStats?.OverrunCount ?? 0
        );
    }

    /// <summary>
    /// Builds clock synchronization statistics from Kalman filter state.
    /// </summary>
    private static ClockSyncStats BuildClockSyncStats(
        ClockSyncStatus clockStatus,
        IAudioPlayer player,
        IClockSynchronizer clockSync)
    {
        // Note: Use Player.OutputLatencyMs directly instead of Pipeline.DetectedOutputLatencyMs
        // because the pipeline's value may not reflect real-time measurements from pa_stream_get_latency()
        return new ClockSyncStats(
            IsSynchronized: clockStatus.IsConverged,
            ClockOffsetMs: clockStatus.OffsetMilliseconds,
            UncertaintyMs: clockStatus.OffsetUncertaintyMicroseconds / 1000.0,
            DriftRatePpm: clockStatus.DriftMicrosecondsPerSecond,
            IsDriftReliable: clockStatus.IsDriftReliable,
            MeasurementCount: clockStatus.MeasurementCount,
            OutputLatencyMs: player.OutputLatencyMs,
            StaticDelayMs: (int)clockSync.StaticDelayMs
        );
    }

    /// <summary>
    /// Builds throughput statistics showing total samples processed.
    /// </summary>
    private static ThroughputStats BuildThroughputStats(AudioBufferStats? bufferStats)
    {
        return new ThroughputStats(
            SamplesWritten: bufferStats?.TotalSamplesWritten ?? 0,
            SamplesRead: bufferStats?.TotalSamplesRead ?? 0,
            SamplesDroppedOverflow: bufferStats?.DroppedSamples ?? 0
        );
    }

    /// <summary>
    /// Builds sync correction statistics showing frame drop/insert mode or adaptive resampling.
    /// </summary>
    /// <param name="bufferStats">Buffer statistics from the pipeline.</param>
    /// <param name="resampleRatio">Current resampling ratio if using adaptive mode (null for frame drop/insert).</param>
    private static SyncCorrectionStats BuildSyncCorrectionStats(AudioBufferStats? bufferStats, double? resampleRatio)
    {
        var syncErrorMs = bufferStats?.SyncErrorMs ?? 0;
        var framesDropped = bufferStats?.SamplesDroppedForSync ?? 0;
        var framesInserted = bufferStats?.SamplesInsertedForSync ?? 0;

        // Determine correction mode
        string correctionMode;
        if (resampleRatio.HasValue)
        {
            // Adaptive resampling mode - ratio indicates direction
            correctionMode = "Adaptive";
        }
        else if (Math.Abs(syncErrorMs) <= SyncToleranceMs)
        {
            correctionMode = "None";
        }
        else if (syncErrorMs > 0)
        {
            correctionMode = "Dropping";
        }
        else
        {
            correctionMode = "Inserting";
        }

        return new SyncCorrectionStats(
            Mode: correctionMode,
            FramesDropped: framesDropped,
            FramesInserted: framesInserted,
            ThresholdMs: resampleRatio.HasValue ? 0 : 15,  // No threshold with adaptive resampling
            ResampleRatio: resampleRatio
        );
    }

    /// <summary>
    /// Builds buffer diagnostics for debugging playback issues.
    /// Determines buffer state based on playback activity and sample flow.
    /// </summary>
    private static BufferDiagnostics BuildBufferDiagnostics(AudioBufferStats? bufferStats, string pipelineState)
    {
        var bufferedMs = bufferStats?.BufferedMs ?? 0;
        var targetMs = bufferStats?.TargetMs ?? 1;  // Avoid divide by zero
        var fillPercent = (int)(bufferedMs / targetMs * 100);
        var isPlaybackActive = bufferStats?.IsPlaybackActive ?? false;
        var samplesRead = bufferStats?.TotalSamplesRead ?? 0;
        var droppedOverflow = bufferStats?.DroppedSamples ?? 0;

        // Determine buffer state for diagnostic purposes
        string bufferState = DetermineBufferState(
            isPlaybackActive, bufferedMs, samplesRead, droppedOverflow);

        return new BufferDiagnostics(
            State: bufferState,
            FillPercent: Math.Min(fillPercent, 100),
            HasReceivedSamples: samplesRead > 0,
            ElapsedSinceFirstReadMs: -1,  // Not available without BufferedAudioSampleSource ref
            ElapsedSinceLastSuccessMs: -1,  // Not available without BufferedAudioSampleSource ref
            DroppedOverflow: droppedOverflow,
            PipelineState: pipelineState,
            SmoothedSyncErrorUs: (long)(bufferStats?.SyncErrorMicroseconds ?? 0)
        );
    }

    /// <summary>
    /// Determines the buffer state description based on playback activity and sample flow.
    /// </summary>
    private static string DetermineBufferState(
        bool isPlaybackActive,
        double bufferedMs,
        long samplesRead,
        long droppedOverflow)
    {
        if (!isPlaybackActive && bufferedMs > 0 && samplesRead == 0)
        {
            return "Waiting for scheduled start";
        }
        else if (!isPlaybackActive && bufferedMs > 0 && samplesRead > 0 && droppedOverflow > 0)
        {
            return "Stalled (was playing, now dropping)";
        }
        else if (!isPlaybackActive && bufferedMs > 0)
        {
            return "Buffered but not playing";
        }
        else if (isPlaybackActive && bufferedMs > 0)
        {
            return "Playing";
        }
        else if (bufferedMs == 0)
        {
            return "Empty";
        }
        else
        {
            return "Unknown";
        }
    }
}
