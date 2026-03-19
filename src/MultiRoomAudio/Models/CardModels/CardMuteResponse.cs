namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Response for card mute operations.
/// </summary>
public record CardMuteResponse(
    bool Success,
    string Message,
    CardOperationStatus Status = CardOperationStatus.Success,
    string? CardName = null,
    bool? IsMuted = null
);
