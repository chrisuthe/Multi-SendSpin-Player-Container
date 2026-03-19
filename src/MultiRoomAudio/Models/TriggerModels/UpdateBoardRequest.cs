using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Request to update a board's settings.
/// </summary>
public class UpdateBoardRequest
{
    /// <summary>
    /// User-friendly display name for this board.
    /// </summary>
    [StringLength(100)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Number of relay channels on the board (1, 2, 4, 8, or 16).
    /// </summary>
    public int? ChannelCount { get; set; }

    /// <summary>
    /// What to do with relays when the board connects on startup.
    /// </summary>
    public RelayStartupBehavior? StartupBehavior { get; set; }

    /// <summary>
    /// What to do with relays when the service shuts down.
    /// </summary>
    public RelayStartupBehavior? ShutdownBehavior { get; set; }
}
