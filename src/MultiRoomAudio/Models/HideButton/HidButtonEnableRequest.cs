namespace MultiRoomAudio.Models.HideButton;

/// <summary>
/// Request to enable/disable HID button support for a device.
/// </summary>
public class HidButtonEnableRequest
{
    /// <summary>Whether to enable HID button support.</summary>
    public bool Enabled { get; set; }
}
