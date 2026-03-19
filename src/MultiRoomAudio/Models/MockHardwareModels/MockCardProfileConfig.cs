namespace MultiRoomAudio.Models.MockHardwareModels;

/// <summary>
/// Configuration for a card profile.
/// </summary>
public class MockCardProfileConfig
{
    /// <summary>
    /// Profile name (e.g., "output:analog-stereo").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Profile description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Number of sinks this profile provides.
    /// </summary>
    public int Sinks { get; set; } = 1;

    /// <summary>
    /// Profile priority (higher = preferred).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether this profile is available.
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Whether this is the active/default profile.
    /// </summary>
    public bool IsDefault { get; set; }
}
