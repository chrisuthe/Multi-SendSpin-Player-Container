namespace MultiRoomAudio.Models.AudioFormat;

/// <summary>
/// Represents an audio format option that can be advertised to the server.
/// </summary>
public record AudioFormatOption(
    string Id,
    string Label,
    string Description
);
