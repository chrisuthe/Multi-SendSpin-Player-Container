namespace MultiRoomAudio.Models.HideButton;

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
