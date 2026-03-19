namespace MultiRoomAudio.Models.PlayerStatus;

/// <summary>
/// Player metrics for monitoring.
/// </summary>
public record PlayerMetrics(
    int BufferLevel,
    int BufferCapacity,
    long SamplesPlayed,
    long Underruns,
    long Overruns
);
