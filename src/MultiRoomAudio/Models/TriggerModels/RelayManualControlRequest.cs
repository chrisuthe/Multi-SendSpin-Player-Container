using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Request to manually control a relay (for testing).
/// </summary>
public class RelayManualControlRequest
{
    /// <summary>
    /// Channel number (1-16).
    /// </summary>
    [Required]
    [Range(1, 16, ErrorMessage = "Channel must be between 1 and 16.")]
    public int Channel { get; set; }

    /// <summary>
    /// Desired relay state.
    /// </summary>
    [Required]
    public bool On { get; set; }
}
