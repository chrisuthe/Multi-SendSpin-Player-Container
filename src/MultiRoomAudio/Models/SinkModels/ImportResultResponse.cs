namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// Response for import operation.
/// </summary>
public record ImportResultResponse(
    List<string> Imported,
    List<string> Errors
);
