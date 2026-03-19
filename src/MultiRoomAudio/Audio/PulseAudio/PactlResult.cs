namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// Result of a pactl command execution.
/// </summary>
public record PactlResult(int ExitCode, string Output, string Error)
{
    /// <summary>
    /// Whether the command succeeded (exit code 0).
    /// </summary>
    public bool Success => ExitCode == 0;
}
