namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Configuration for a persisted card profile selection.
/// </summary>
public class CardProfileConfiguration
{
    /// <summary>
    /// Card name (PulseAudio card name, not index, for stability across restarts).
    /// </summary>
    public required string CardName { get; set; }

    /// <summary>
    /// Selected profile name.
    /// </summary>
    public required string ProfileName { get; set; }

    /// <summary>
    /// Boot mute preference (true = muted, false = unmuted).
    /// </summary>
    public bool? BootMuted { get; set; }
}
