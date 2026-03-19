using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// Request to play a test tone.
/// </summary>
/// <param name="FrequencyHz">Tone frequency in Hz (20-20000). Default: 1000Hz.</param>
/// <param name="DurationMs">Tone duration in milliseconds (100-10000). Default: 1500ms.</param>
/// <param name="ChannelName">Optional channel name for multi-channel devices.</param>
public record TestToneRequest(
    [property: Range(20, 20000, ErrorMessage = "FrequencyHz must be between 20 and 20000.")]
    int? FrequencyHz = null,
    [property: Range(100, 10000, ErrorMessage = "DurationMs must be between 100 and 10000.")]
    int? DurationMs = null,
    string? ChannelName = null);
