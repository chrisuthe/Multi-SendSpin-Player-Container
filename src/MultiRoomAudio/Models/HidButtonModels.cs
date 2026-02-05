using System.Runtime.InteropServices;

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
/// </summary>
public class HidButtonDeviceState
{
    /// <summary>Device sink name in PulseAudio.</summary>
    public string SinkName { get; set; } = string.Empty;

    /// <summary>Whether HID buttons are enabled for this device.</summary>
    public bool Enabled { get; set; }

    /// <summary>Path to the input device (e.g., /dev/input/by-id/...).</summary>
    public string? InputDevicePath { get; set; }

    /// <summary>Name of the player associated with this device (for applying volume/mute changes).</summary>
    public string? PlayerName { get; set; }

    /// <summary>Cancellation token source for the HID event reader task.</summary>
    public CancellationTokenSource? ReaderCts { get; set; }

    /// <summary>The task that reads HID events.</summary>
    public Task? ReaderTask { get; set; }

    /// <summary>Last known mute state (for toggle logic).</summary>
    public bool IsMuted { get; set; }
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

/// <summary>
/// Linux input_event struct for reading HID events from /dev/input/eventX.
/// On 64-bit systems, this struct is 24 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct LinuxInputEvent
{
    /// <summary>Seconds since epoch (timeval.tv_sec).</summary>
    public long Seconds;

    /// <summary>Microseconds (timeval.tv_usec).</summary>
    public long Microseconds;

    /// <summary>Event type (e.g., EV_KEY = 1).</summary>
    public ushort Type;

    /// <summary>Event code (e.g., KEY_MUTE = 113).</summary>
    public ushort Code;

    /// <summary>Event value (1 = pressed, 0 = released, 2 = repeat).</summary>
    public int Value;
}

/// <summary>
/// Constants for Linux input event types and key codes.
/// </summary>
public static class LinuxInputConstants
{
    /// <summary>Size of input_event struct on 64-bit Linux.</summary>
    public const int InputEventSize = 24;

    // Event types
    public const ushort EV_SYN = 0;
    public const ushort EV_KEY = 1;

    // Key codes for multimedia keys
    public const ushort KEY_MUTE = 113;
    public const ushort KEY_VOLUMEDOWN = 114;
    public const ushort KEY_VOLUMEUP = 115;

    // Key event values
    public const int KEY_RELEASED = 0;
    public const int KEY_PRESSED = 1;
    public const int KEY_REPEAT = 2;
}
