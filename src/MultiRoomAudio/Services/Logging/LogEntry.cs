namespace MultiRoomAudio.Services.Logging;

/// <summary>
/// Represents a single log entry with timestamp and metadata.
/// </summary>
public record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    LogCategory Category,
    string Message,
    string? Exception = null
);
