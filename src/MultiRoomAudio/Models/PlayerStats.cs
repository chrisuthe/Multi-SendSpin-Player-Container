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
    ResamplerStats Resampler
);

/// <summary>
/// Audio format information for input and output.
/// </summary>
public record AudioFormatStats(
    string InputFormat,
    int InputSampleRate,
    int InputChannels,
    string? InputBitrate,
    string OutputFormat,
    int OutputSampleRate,
    int OutputChannels,
    int OutputBitDepth
);

/// <summary>
/// Sync status and correction information.
/// </summary>
public record SyncStats(
    double SyncErrorMs,
    string CorrectionMode,
    double PlaybackRate,
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
    long SamplesDroppedSync,
    long SamplesInsertedSync,
    long SamplesDroppedOverflow
);

/// <summary>
/// Audio format conversion information.
/// Shows input vs output sample rates. PulseAudio handles format conversion.
/// </summary>
public record ResamplerStats(
    int InputRate,
    int OutputRate,
    string Quality,
    double EffectiveRatio
);
