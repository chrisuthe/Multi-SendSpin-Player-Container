namespace MultiRoomAudio.Services.Configuration;

/// <summary>
/// Persisted device configuration with alias and stable identifiers.
/// Used for re-matching devices when PulseAudio sink names change across reboots.
/// </summary>
public class DeviceConfiguration
{
    /// <summary>
    /// User-defined friendly name for the device (e.g., "Kitchen Speaker").
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// Last known PulseAudio sink name (may become stale after reboot).
    /// </summary>
    public string? LastKnownSinkName { get; set; }

    /// <summary>
    /// Stable identifiers for re-matching devices across reboots.
    /// </summary>
    public DeviceIdentifiersConfig? Identifiers { get; set; }

    /// <summary>
    /// When this device was first seen.
    /// </summary>
    public DateTime? FirstSeen { get; set; }

    /// <summary>
    /// When this device was last matched/seen.
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Whether this device is hidden from player creation.
    /// Hidden devices don't appear in dropdowns by default (useful for HDMI outputs).
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Maximum volume limit for this device (0-100%).
    /// Applied to the PulseAudio sink at startup and when changed via API.
    /// </summary>
    public int? MaxVolume { get; set; }

    /// <summary>
    /// HID button configuration for hardware volume/mute controls.
    /// </summary>
    public HidButtonConfiguration? HidButtons { get; set; }
}
