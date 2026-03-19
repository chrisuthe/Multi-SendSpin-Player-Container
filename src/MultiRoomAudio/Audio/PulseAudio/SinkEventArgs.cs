namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// Event args for sink appearance/disappearance events.
/// </summary>
/// <param name="Index">PulseAudio sink index.</param>
public record SinkEventArgs(uint Index);
