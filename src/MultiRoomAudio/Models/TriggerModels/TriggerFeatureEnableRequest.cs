namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Request to enable/disable the trigger feature.
/// </summary>
public class TriggerFeatureEnableRequest
{
    /// <summary>
    /// Whether to enable the trigger feature.
    /// </summary>
    public bool Enabled { get; set; }
}
