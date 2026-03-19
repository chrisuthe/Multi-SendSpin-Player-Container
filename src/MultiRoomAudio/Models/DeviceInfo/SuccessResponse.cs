namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Success response format.
/// </summary>
public record SuccessResponse(
    bool Success,
    string Message
);
