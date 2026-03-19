namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Health check response.
/// </summary>
public record HealthResponse(
    string Status,
    DateTime Timestamp,
    string Version
);
