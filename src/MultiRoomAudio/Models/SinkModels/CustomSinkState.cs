namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// State of a custom sink.
/// </summary>
public enum CustomSinkState
{
    /// <summary>Configuration created but not yet loaded.</summary>
    Created,
    /// <summary>Currently loading the module.</summary>
    Loading,
    /// <summary>Module loaded successfully in PulseAudio.</summary>
    Loaded,
    /// <summary>Failed to load or encountered an error.</summary>
    Error,
    /// <summary>Currently unloading the module.</summary>
    Unloading
}
