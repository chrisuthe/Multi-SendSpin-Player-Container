namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Response containing device list.
/// </summary>
public record DevicesListResponse(
    List<AudioDevice> Devices,
    int Count
);
