namespace MultiRoomAudio.Models.PlayerStats;

/// <summary>
/// Buffer level and underrun/overrun statistics.
/// </summary>
public record BufferStatsInfo(
    int BufferedMs,
    int TargetMs,
    long Underruns,
    long Overruns
);
