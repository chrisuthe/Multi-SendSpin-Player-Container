namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request to set mute state.
/// </summary>
/// <param name="Muted">True to mute the player, false to unmute.</param>
public record MuteRequest(bool Muted);
