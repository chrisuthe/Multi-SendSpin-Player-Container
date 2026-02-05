namespace MultiRoomAudio.Models;

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

/// <summary>
/// State tracking for a device with HID buttons enabled.
/// Used for feedback loop prevention and debouncing.
/// </summary>
public class HidButtonDeviceState
{
    /// <summary>Device sink name in PulseAudio.</summary>
    public string SinkName { get; set; } = string.Empty;

    /// <summary>Whether HID buttons are enabled for this device.</summary>
    public bool Enabled { get; set; }

    /// <summary>Loaded module-mmkbd-evdev module index, if any.</summary>
    public int? ModuleIndex { get; set; }

    /// <summary>Path to the input device (e.g., /dev/input/by-id/...).</summary>
    public string? InputDevicePath { get; set; }

    /// <summary>Last known volume percentage for this sink.</summary>
    public int LastKnownVolume { get; set; }

    /// <summary>Last known mute state for this sink.</summary>
    public bool LastKnownMuted { get; set; }

    /// <summary>Timestamp of last processed change.</summary>
    public DateTime LastChangeTime { get; set; }

    /// <summary>
    /// Flag indicating we're currently processing a hardware-initiated change.
    /// Used to prevent feedback loops.
    /// </summary>
    public bool IsProcessingChange { get; set; }

    /// <summary>Pending volume from debounced events.</summary>
    public int? PendingVolume { get; set; }

    /// <summary>Pending mute state from debounced events.</summary>
    public bool? PendingMuted { get; set; }
}

// Note: HidButtonConfiguration is defined in ConfigurationService.cs for YAML serialization

/// <summary>
/// Response for HID button status of a device.
/// </summary>
public record HidButtonStatusResponse(
    /// <summary>Device ID (sink name).</summary>
    string DeviceId,
    /// <summary>Whether the device has an available HID input interface.</summary>
    bool HidButtonsAvailable,
    /// <summary>Whether HID button support is enabled by the user.</summary>
    bool HidButtonsEnabled,
    /// <summary>Path to the input device, if available.</summary>
    string? InputDevicePath,
    /// <summary>Loaded module index, if enabled and active.</summary>
    int? ModuleIndex,
    /// <summary>Error message if there was a problem.</summary>
    string? ErrorMessage
);

/// <summary>
/// Request to enable/disable HID button support for a device.
/// </summary>
public class HidButtonEnableRequest
{
    /// <summary>Whether to enable HID button support.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Response after enabling/disabling HID buttons.
/// </summary>
public record HidButtonEnableResponse(
    /// <summary>Whether the operation succeeded.</summary>
    bool Success,
    /// <summary>Device ID (sink name).</summary>
    string DeviceId,
    /// <summary>New enabled state.</summary>
    bool HidButtonsEnabled,
    /// <summary>Path to the input device, if enabled.</summary>
    string? InputDevicePath,
    /// <summary>Loaded module index, if enabled.</summary>
    int? ModuleIndex,
    /// <summary>Message describing the result.</summary>
    string Message
);
