namespace MultiRoomAudio.Services.Configuration;

/// <summary>
/// Configuration for HID buttons on a device.
/// Persisted in devices.yaml.
/// </summary>
public class HidButtonConfiguration
{
    /// <summary>Whether HID button support is enabled for this device.</summary>
    public bool Enabled { get; set; }

    /// <summary>Last known input device path (for reconnection).</summary>
    public string? LastKnownInputPath { get; set; }
}
