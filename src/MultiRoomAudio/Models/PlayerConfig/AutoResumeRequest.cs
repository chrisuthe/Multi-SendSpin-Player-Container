namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request to enable/disable auto-resume on device reconnection.
/// </summary>
/// <param name="Enabled">True to enable auto-resume, false to disable.</param>
public record AutoResumeRequest(bool Enabled);
