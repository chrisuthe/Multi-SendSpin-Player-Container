namespace MultiRoomAudio.Services.Logging;

/// <summary>
/// Options for querying logs.
/// </summary>
public record LogQueryOptions(
    LogLevel? MinLevel = null,
    LogCategory? Category = null,
    string? SearchText = null,
    DateTime? StartTime = null,
    DateTime? EndTime = null,
    int Skip = 0,
    int Take = 100,
    bool NewestFirst = true
);
