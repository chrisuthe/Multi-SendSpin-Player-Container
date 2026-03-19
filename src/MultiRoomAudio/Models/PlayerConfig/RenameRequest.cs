using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request to rename a player.
/// </summary>
/// <param name="NewName">The new name for the player.</param>
public record RenameRequest(
    [property: Required(ErrorMessage = "New player name is required.")]
    [property: StringLength(100, MinimumLength = 1, ErrorMessage = "Player name must be between 1 and 100 characters.")]
    [property: RegularExpression(@"^[^\x00-\x1F\x7F]+$", ErrorMessage = "Player name cannot contain control characters.")]
    string NewName);
