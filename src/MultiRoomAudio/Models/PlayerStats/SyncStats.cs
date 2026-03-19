namespace MultiRoomAudio.Models.PlayerStats;

/// <summary>
/// Sync status information.
/// </summary>
public record SyncStats(
    double SyncErrorMs,
    bool IsWithinTolerance,
    bool IsPlaybackActive
);
