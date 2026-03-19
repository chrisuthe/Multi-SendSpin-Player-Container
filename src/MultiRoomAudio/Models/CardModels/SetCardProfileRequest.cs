using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Request to set a card's active profile.
/// </summary>
public class SetCardProfileRequest
{
    /// <summary>
    /// Profile name to activate (e.g., "output:analog-surround-71").
    /// Must be one of the available profiles for the card.
    /// </summary>
    [Required(ErrorMessage = "Profile name is required.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Profile name must be 1-200 characters.")]
    public required string Profile { get; set; }
}
