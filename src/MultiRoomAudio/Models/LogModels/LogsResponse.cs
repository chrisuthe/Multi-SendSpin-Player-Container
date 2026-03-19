namespace MultiRoomAudio.Models.LogModels;

/// <summary>
/// Response containing a list of log entries.
/// </summary>
public record LogsResponse(
    List<LogEntryDto> Entries,
    int TotalCount,
    int Skip,
    int Take
);
