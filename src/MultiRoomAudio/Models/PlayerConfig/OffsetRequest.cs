using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request to update offset.
/// </summary>
/// <param name="DelayMs">Audio delay offset in milliseconds (-10000 to 10000).</param>
public record OffsetRequest(
    [property: Range(-10000, 10000, ErrorMessage = "DelayMs must be between -10000 and 10000 milliseconds.")]
    int DelayMs);
