namespace MultiRoomAudio.Models.PlayerStatus;

/// <summary>
/// Player state enumeration.
/// </summary>
public enum PlayerState
{
    Created,
    Starting,
    Connecting,
    Connected,
    Buffering,
    Playing,
    Paused,
    Stopped,
    Error,
    Reconnecting,
    WaitingForServer,
    /// <summary>
    /// Player stopped due to audio device loss (USB unplug).
    /// Waiting for the device to reconnect.
    /// </summary>
    WaitingForDevice
}
