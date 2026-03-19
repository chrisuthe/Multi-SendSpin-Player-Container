namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// State of an individual relay channel.
/// </summary>
public enum RelayState
{
    /// <summary>Relay is off (NO contacts open).</summary>
    Off,
    /// <summary>Relay is on (NO contacts closed).</summary>
    On,
    /// <summary>Relay state is unknown.</summary>
    Unknown
}
