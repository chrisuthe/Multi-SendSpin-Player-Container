namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Response containing list of sound cards.
/// </summary>
public record CardsListResponse(
    List<PulseAudioCard> Cards,
    int Count
);
