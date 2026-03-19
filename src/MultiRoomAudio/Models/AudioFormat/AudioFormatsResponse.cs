namespace MultiRoomAudio.Models.AudioFormat;

/// <summary>
/// Response containing available audio format options.
/// </summary>
public record AudioFormatsResponse(
    List<AudioFormatOption> Formats
);
