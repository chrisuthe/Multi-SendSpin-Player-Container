using MultiRoomAudio.Models.DeviceInfo;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Device capabilities with source information.
/// </summary>
public record DeviceCapabilitiesWithSource(
    DeviceCapabilities Capabilities,
    CapabilitySource Source
);
