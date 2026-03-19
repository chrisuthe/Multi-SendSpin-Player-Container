namespace MultiRoomAudio.Models.PlayerStatus;

/// <summary>
/// Current track information from Music Assistant.
/// </summary>
public record TrackInfo(
    string? Title,
    string? Artist,
    string? Album,
    string? ArtworkUrl,
    double? DurationSeconds,
    double? PositionSeconds
);
