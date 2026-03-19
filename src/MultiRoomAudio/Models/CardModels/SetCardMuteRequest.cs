using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Request to set a card's mute state.
/// </summary>
public class SetCardMuteRequest
{
    /// <summary>
    /// True to mute the card, false to unmute it.
    /// </summary>
    [Required(ErrorMessage = "Mute state is required.")]
    public bool Muted { get; set; }
}
