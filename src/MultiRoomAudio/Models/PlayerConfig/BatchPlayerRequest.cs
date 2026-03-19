namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Single player creation request for batch operations.
/// </summary>
public record BatchPlayerRequest(
    string Name,
    string? Device = null,
    int? Volume = null,
    bool? Autostart = null);
