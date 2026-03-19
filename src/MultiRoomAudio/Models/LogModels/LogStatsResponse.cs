namespace MultiRoomAudio.Models.LogModels;

/// <summary>
/// Response containing log statistics.
/// </summary>
public record LogStatsResponse(
    Dictionary<string, int> ByLevel,
    Dictionary<string, int> ByCategory,
    int TotalEntries,
    DateTime? OldestEntry,
    DateTime? NewestEntry
);
