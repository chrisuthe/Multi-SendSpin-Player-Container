using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Configuration for a single trigger channel (1-16).
/// Maps a relay to a custom sink with configurable off-delay.
/// </summary>
public class TriggerConfiguration
{
    /// <summary>
    /// Channel number (1-16).
    /// </summary>
    [Range(1, 16)]
    public int Channel { get; set; }

    /// <summary>
    /// Name of the custom sink that triggers this relay.
    /// Null or empty means this trigger is not assigned.
    /// </summary>
    public string? CustomSinkName { get; set; }

    /// <summary>
    /// Delay in seconds before turning off the relay after playback stops.
    /// Default is 60 seconds (1 minute).
    /// </summary>
    [Range(0, 3600)]
    public int OffDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Optional friendly name for this trigger/zone.
    /// </summary>
    [StringLength(100)]
    public string? ZoneName { get; set; }
}
