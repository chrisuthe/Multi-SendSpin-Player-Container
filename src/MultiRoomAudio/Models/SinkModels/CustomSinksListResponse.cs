namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// List response for custom sinks.
/// </summary>
public record CustomSinksListResponse(
    List<CustomSinkResponse> Sinks,
    int Count
);
