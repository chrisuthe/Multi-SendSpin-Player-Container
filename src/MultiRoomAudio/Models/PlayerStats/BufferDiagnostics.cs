namespace MultiRoomAudio.Models.PlayerStats;

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
