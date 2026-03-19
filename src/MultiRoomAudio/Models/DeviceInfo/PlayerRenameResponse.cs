namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Response for player rename operations.
/// Indicates whether restart is required for changes to propagate.
/// </summary>
public record PlayerRenameResponse(
    bool Success,
    string Message,
    string NewName,
    bool RestartRequired = true,
    string? RestartHint = "Restart the player for the name change to appear in Music Assistant."
);
