namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// Type of custom PulseAudio sink.
/// </summary>
public enum CustomSinkType
{
    /// <summary>module-combine-sink - Merge multiple outputs.</summary>
    Combine,
    /// <summary>module-remap-sink - Channel extraction/remapping.</summary>
    Remap
}
