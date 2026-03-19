namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Response for card boot mute operations.
/// </summary>
public record CardBootMuteResponse(
    bool Success,
    string Message,
    string? CardName = null,
    bool? BootMuted = null
);
