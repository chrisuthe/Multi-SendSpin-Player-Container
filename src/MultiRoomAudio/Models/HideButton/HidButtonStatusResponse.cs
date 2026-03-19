namespace MultiRoomAudio.Models.HideButton;

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
