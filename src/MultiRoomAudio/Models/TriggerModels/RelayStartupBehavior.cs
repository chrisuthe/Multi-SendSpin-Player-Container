namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// What to do with relays when the board connects on startup.
/// </summary>
public enum RelayStartupBehavior
{
    /// <summary>Turn all relays OFF on startup (safest default).</summary>
    AllOff,
    /// <summary>Turn all relays ON on startup.</summary>
    AllOn,
    /// <summary>Don't change relay state on startup (preserve hardware state).</summary>
    NoChange
}
