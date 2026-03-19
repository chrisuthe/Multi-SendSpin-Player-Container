namespace MultiRoomAudio.Models.PlayerStats;

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
    BufferDiagnostics Diagnostics,
    /// <summary>SDK version for debugging.</summary>
    string SdkVersion = "unknown",
    /// <summary>Server time matching log timestamps.</summary>
    string ServerTime = ""
);
