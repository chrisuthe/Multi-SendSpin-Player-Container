namespace MultiRoomAudio.Models.HideButton;

/// <summary>
/// Event arguments for sink change events from PulseAudio.
/// </summary>
public record SinkChangeEventArgs(
    /// <summary>PulseAudio sink index.</summary>
    int SinkIndex,
    /// <summary>PulseAudio sink name.</summary>
    string SinkName,
    /// <summary>Current volume percentage (0-100).</summary>
    int VolumePercent,
    /// <summary>Whether the sink is muted.</summary>
    bool IsMuted,
    /// <summary>Timestamp of the event.</summary>
    DateTime Timestamp
);
