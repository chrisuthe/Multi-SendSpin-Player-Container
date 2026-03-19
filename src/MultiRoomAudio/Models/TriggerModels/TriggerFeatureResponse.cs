namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Response for the overall trigger feature status.
/// Supports multiple boards.
/// </summary>
public record TriggerFeatureResponse(
    bool Enabled,
    List<TriggerBoardResponse> Boards,
    int TotalChannels
);
