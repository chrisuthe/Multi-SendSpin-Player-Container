using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request to set volume.
/// </summary>
/// <param name="Volume">Volume level from 0 to 100.</param>
public record VolumeRequest(
    [property: Range(0, 100, ErrorMessage = "Volume must be between 0 and 100.")]
    int Volume);
