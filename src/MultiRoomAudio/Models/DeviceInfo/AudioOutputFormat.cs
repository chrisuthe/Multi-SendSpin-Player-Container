namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Configurable audio output format for a player.
/// </summary>
public record AudioOutputFormat(
    int SampleRate,
    int BitDepth,
    int Channels = 2
)
{
    /// <summary>
    /// Default format: 48kHz, 32-bit float, stereo.
    /// </summary>
    public static AudioOutputFormat Default => new(48000, 32, 2);

    /// <summary>
    /// High-resolution format: 192kHz, 24-bit, stereo.
    /// </summary>
    public static AudioOutputFormat HiRes192 => new(192000, 24, 2);

    /// <summary>
    /// High-resolution format: 96kHz, 24-bit, stereo.
    /// </summary>
    public static AudioOutputFormat HiRes96 => new(96000, 24, 2);
}
