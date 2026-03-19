namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// State of the relay trigger feature.
/// </summary>
public enum TriggerFeatureState
{
    /// <summary>Feature is disabled.</summary>
    Disabled,
    /// <summary>Feature is enabled but relay board not connected.</summary>
    Disconnected,
    /// <summary>Feature is enabled and relay board is connected.</summary>
    Connected,
    /// <summary>Feature is enabled but encountered an error.</summary>
    Error
}
