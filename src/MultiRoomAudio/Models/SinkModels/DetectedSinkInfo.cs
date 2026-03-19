namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// Information about a detected sink in default.pa.
/// </summary>
public record DetectedSinkInfo(
    int LineNumber,
    string Type,
    string Name,
    string? Description,
    List<string>? Slaves,
    string? MasterSink,
    string Preview
);
