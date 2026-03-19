using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Request to update the channel count.
/// </summary>
public class ChannelCountRequest
{
    /// <summary>
    /// Number of relay channels (1, 2, 4, 8, or 16).
    /// </summary>
    [Required]
    public int ChannelCount { get; set; }
}
