namespace MultiRoomAudio.Models;

/// <summary>
/// Complete stats response for a player (Stats for Nerds).
/// </summary>
public record PlayerStatsResponse(
    string PlayerName,
    AudioFormatStats AudioFormat,
    SyncStats Sync,
    BufferStatsInfo Buffer,
    ClockSyncStats ClockSync,
    ThroughputStats Throughput,
    SyncCorrectionStats Correction,
    BufferDiagnostics Diagnostics
);

/// <summary>
/// Audio format information for input, output, and hardware sink.
/// </summary>
public record AudioFormatStats(
    string InputFormat,
    int InputSampleRate,
    int InputChannels,
    string? InputBitrate,
    string OutputFormat,
    int OutputSampleRate,
    int OutputChannels,
    int OutputBitDepth,
    // Hardware sink format (what PulseAudio negotiated with the device)
    string? HardwareFormat = null,      // e.g., "S32LE", "S24LE", "FLOAT32LE"
    int? HardwareSampleRate = null,     // Actual sink sample rate
    int? HardwareBitDepth = null        // Derived bit depth (16, 24, or 32)
);

/// <summary>
/// Sync status information.
/// </summary>
public record SyncStats(
    double SyncErrorMs,
    bool IsWithinTolerance,
    bool IsPlaybackActive
);

/// <summary>
/// Buffer level and underrun/overrun statistics.
/// </summary>
public record BufferStatsInfo(
    int BufferedMs,
    int TargetMs,
    long Underruns,
    long Overruns
);

/// <summary>
/// Clock synchronization details.
/// </summary>
public record ClockSyncStats(
    bool IsSynchronized,
    double ClockOffsetMs,
    double UncertaintyMs,
    double DriftRatePpm,
    bool IsDriftReliable,
    int MeasurementCount,
    int OutputLatencyMs,
    int StaticDelayMs
);

/// <summary>
/// Sample throughput counters.
/// </summary>
public record ThroughputStats(
    long SamplesWritten,
    long SamplesRead,
    long SamplesDroppedOverflow
);

/// <summary>
/// Sync correction statistics.
/// Uses frame drop/insert when sync error exceeds 5ms threshold.
/// </summary>
public record SyncCorrectionStats(
    string Mode,
    long FramesDropped,
    long FramesInserted,
    int ThresholdMs
);

/// <summary>
/// Buffer diagnostic information for debugging playback issues.
/// Shows why the buffer might not be releasing samples.
/// </summary>
public record BufferDiagnostics(
    /// <summary>Current buffer state description.</summary>
    string State,
    /// <summary>Buffer fill percentage (0-100).</summary>
    int FillPercent,
    /// <summary>Whether samples have ever been successfully read from the buffer.</summary>
    bool HasReceivedSamples,
    /// <summary>Time since first read attempt in milliseconds.</summary>
    long ElapsedSinceFirstReadMs,
    /// <summary>Time since last successful sample read in milliseconds, or -1 if never.</summary>
    long ElapsedSinceLastSuccessMs,
    /// <summary>Samples dropped due to buffer overflow (too full, SDK dropping old data).</summary>
    long DroppedOverflow,
    /// <summary>Pipeline state from SDK.</summary>
    string PipelineState,
    /// <summary>Smoothed sync error in microseconds.</summary>
    long SmoothedSyncErrorUs
);
