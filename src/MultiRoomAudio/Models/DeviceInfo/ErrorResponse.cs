namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Error response format.
/// </summary>
public record ErrorResponse(
    bool Success,
    string Message
);
