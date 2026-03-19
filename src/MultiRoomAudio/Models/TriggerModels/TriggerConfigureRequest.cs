using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Request to configure a single trigger channel.
/// </summary>
public class TriggerConfigureRequest
{
    /// <summary>
    /// Channel number (1-16).
    /// </summary>
    [Required]
    [Range(1, 16, ErrorMessage = "Channel must be between 1 and 16.")]
    public int Channel { get; set; }

    /// <summary>
    /// Custom sink name to assign to this trigger.
    /// Set to null or empty to unassign.
    /// </summary>
    public string? CustomSinkName { get; set; }

    /// <summary>
    /// Off delay in seconds (0-3600).
    /// </summary>
    [Range(0, 3600, ErrorMessage = "Off delay must be between 0 and 3600 seconds.")]
    public int OffDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Optional friendly name for this zone.
    /// </summary>
    [StringLength(100, ErrorMessage = "Zone name must be 100 characters or less.")]
    public string? ZoneName { get; set; }
}
