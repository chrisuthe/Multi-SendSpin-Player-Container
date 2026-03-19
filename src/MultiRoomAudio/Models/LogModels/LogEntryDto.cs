namespace MultiRoomAudio.Models.LogModels;

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
