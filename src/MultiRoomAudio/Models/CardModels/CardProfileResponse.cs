namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Response for card profile operations.
/// </summary>
public record CardProfileResponse(
    bool Success,
    string Message,
    CardOperationStatus Status = CardOperationStatus.Success,
    string? CardName = null,
    string? ActiveProfile = null,
    string? PreviousProfile = null
);
