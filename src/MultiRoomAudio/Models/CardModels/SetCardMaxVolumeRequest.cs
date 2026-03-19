using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Request to set a card's maximum volume limit.
/// </summary>
public class SetCardMaxVolumeRequest
{
    /// <summary>
    /// Maximum volume limit (0-100), or null to clear the limit.
    /// </summary>
    [Range(0, 100, ErrorMessage = "MaxVolume must be between 0 and 100.")]
    public int? MaxVolume { get; set; }
}
