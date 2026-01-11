using MultiRoomAudio.Services;

namespace MultiRoomAudio.Models;

/// <summary>
/// Log entry data transfer object for API responses.
/// </summary>
public record LogEntryDto(
    string Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception
);

/// <summary>
/// Response containing a list of log entries.
/// </summary>
public record LogsResponse(
    List<LogEntryDto> Entries,
    int TotalCount,
    int Skip,
    int Take
);

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

/// <summary>
/// Extension methods for log model conversions.
/// </summary>
public static class LogModelExtensions
{
    /// <summary>
    /// Converts a LogEntry to a LogEntryDto.
    /// </summary>
    public static LogEntryDto ToDto(this LogEntry entry)
    {
        return new LogEntryDto(
            entry.Timestamp.ToString("o"),
            entry.Level.ToString(),
            entry.Category.ToString(),
            entry.Message,
            entry.Exception
        );
    }

    /// <summary>
    /// Converts LogStats to a LogStatsResponse.
    /// </summary>
    public static LogStatsResponse ToResponse(this LogStats stats)
    {
        return new LogStatsResponse(
            stats.ByLevel,
            stats.ByCategory,
            stats.TotalEntries,
            stats.OldestEntry,
            stats.NewestEntry
        );
    }
}
