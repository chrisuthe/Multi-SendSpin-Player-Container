namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request for batch player creation.
/// </summary>
public record BatchCreatePlayersRequest(List<BatchPlayerRequest>? Players);
