namespace MultiRoomAudio.Models;

/// <summary>
/// Represents an audio format option that can be advertised to the server.
/// </summary>
public record AudioFormatOption(
    string Id,
    string Label,
    string Description
);

/// <summary>
/// Response containing available audio format options.
/// </summary>
public record AudioFormatsResponse(
    List<AudioFormatOption> Formats
);
