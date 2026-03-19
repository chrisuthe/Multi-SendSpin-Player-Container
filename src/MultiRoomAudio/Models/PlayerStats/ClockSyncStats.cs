namespace MultiRoomAudio.Models.PlayerStats;

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
    int StaticDelayMs,
    /// <summary>Active timing source: "audio-clock", "monotonic", or "wall-clock".</summary>
    string TimingSource = "unknown"
);
