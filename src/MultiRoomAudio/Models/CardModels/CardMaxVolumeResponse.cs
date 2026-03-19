namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Response for card max volume operations.
/// </summary>
public record CardMaxVolumeResponse(
    bool Success,
    string Message,
    string? CardName = null,
    int? MaxVolume = null
);
