namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Represents a failed player creation in a batch operation.
/// </summary>
public record BatchPlayerFailure(string Name, string Error);
