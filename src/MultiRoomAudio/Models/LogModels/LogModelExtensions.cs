using MultiRoomAudio.Services.Logging;

namespace MultiRoomAudio.Models.LogModels;

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
