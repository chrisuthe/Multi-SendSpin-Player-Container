namespace MultiRoomAudio.Models.PlayerStats;

/// <summary>
/// Sample throughput counters.
/// </summary>
public record ThroughputStats(
    long SamplesWritten,
    long SamplesRead,
    long SamplesDroppedOverflow
);
