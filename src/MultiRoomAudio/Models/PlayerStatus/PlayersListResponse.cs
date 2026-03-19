namespace MultiRoomAudio.Models.PlayerStatus;

/// <summary>
/// List response wrapper.
/// </summary>
public record PlayersListResponse(
    List<PlayerResponse> Players,
    int Count
);
