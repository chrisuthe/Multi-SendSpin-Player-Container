namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// Response for import scan results.
/// </summary>
public record ImportScanResponse(
    int Found,
    List<DetectedSinkInfo> Sinks
);
