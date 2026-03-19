using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// Request to set device maximum volume limit.
/// </summary>
/// <param name="MaxVolume">Maximum volume limit (0-100), or null to clear the limit.</param>
public record DeviceMaxVolumeRequest(
    [property: Range(0, 100, ErrorMessage = "MaxVolume must be between 0 and 100.")]
    int? MaxVolume);
