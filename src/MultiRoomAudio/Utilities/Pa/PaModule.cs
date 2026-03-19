namespace MultiRoomAudio.Utilities.Pa;

/// <summary>
/// Represents a loaded PulseAudio module.
/// </summary>
public record PaModule(int Index, string Name, string Arguments);
