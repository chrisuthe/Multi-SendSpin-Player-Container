namespace MultiRoomAudio.Models.PlayerStats;

/// <summary>
/// Sync correction statistics.
/// Uses frame drop/insert when sync error exceeds 15ms threshold.
/// </summary>
public record SyncCorrectionStats(
    string Mode,
    long FramesDropped,
    long FramesInserted,
    int ThresholdMs
);
