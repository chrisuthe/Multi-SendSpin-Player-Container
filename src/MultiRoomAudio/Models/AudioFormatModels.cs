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
/// <param name="Formats">Selectable formats, best quality first.</param>
/// <param name="DefaultFormatId">Id of the format a player with no saved preference advertises.</param>
public record AudioFormatsResponse(
    List<AudioFormatOption> Formats,
    string? DefaultFormatId = null
);
