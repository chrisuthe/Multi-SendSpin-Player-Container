using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Request to set a card's boot mute preference.
/// </summary>
public class SetCardBootMuteRequest
{
    /// <summary>
    /// True to mute the card at boot, false to unmute it at boot.
    /// </summary>
    [Required(ErrorMessage = "Boot mute state is required.")]
    public bool Muted { get; set; }
}
