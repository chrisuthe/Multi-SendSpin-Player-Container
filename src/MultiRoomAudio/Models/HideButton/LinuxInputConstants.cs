namespace MultiRoomAudio.Models.HideButton;

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
