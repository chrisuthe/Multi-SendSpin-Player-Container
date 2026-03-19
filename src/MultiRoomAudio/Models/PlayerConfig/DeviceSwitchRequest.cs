using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request to switch audio device.
/// </summary>
/// <param name="Device">The device identifier to switch to. If null, the default device will be used.</param>
public record DeviceSwitchRequest(
    [property: StringLength(100, ErrorMessage = "Device name cannot exceed 100 characters.")]
    string? Device);
