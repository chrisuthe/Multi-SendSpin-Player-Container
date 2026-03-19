using System.Runtime.InteropServices;

namespace MultiRoomAudio.Models.HideButton;

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
