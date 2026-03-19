namespace MultiRoomAudio.Models.HideButton;

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
