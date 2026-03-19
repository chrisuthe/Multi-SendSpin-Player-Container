namespace MultiRoomAudio.Services.Logging;

/// <summary>
/// Statistics about the current logs.
/// </summary>
public record LogStats(
    Dictionary<string, int> ByLevel,
    Dictionary<string, int> ByCategory,
    int TotalEntries,
    DateTime? OldestEntry,
    DateTime? NewestEntry
);
