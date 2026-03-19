namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// Available PulseAudio channel names for UI channel picker.
/// </summary>
public static class PulseAudioChannels
{
    /// <summary>Standard stereo channels.</summary>
    public static readonly string[] StereoChannels =
        ["front-left", "front-right"];

    /// <summary>Quad surround channels.</summary>
    public static readonly string[] QuadChannels =
        ["front-left", "front-right", "rear-left", "rear-right"];

    /// <summary>5.1 surround channels.</summary>
    public static readonly string[] Surround51Channels =
        ["front-left", "front-right", "front-center", "lfe", "rear-left", "rear-right"];

    /// <summary>7.1 surround channels.</summary>
    public static readonly string[] Surround71Channels =
        ["front-left", "front-right", "front-center", "lfe", "rear-left", "rear-right", "side-left", "side-right"];

    /// <summary>All available channel names.</summary>
    public static readonly string[] AllChannels =
        ["front-left", "front-right", "front-center", "lfe", "rear-left", "rear-right", "rear-center", "side-left", "side-right", "mono", "left", "right", "center", "subwoofer"];

    /// <summary>
    /// Get channel presets for a given channel count.
    /// </summary>
    public static string[] GetChannelsForCount(int count)
    {
        return count switch
        {
            1 => ["mono"],
            2 => StereoChannels,
            4 => QuadChannels,
            6 => Surround51Channels,
            8 => Surround71Channels,
            _ => AllChannels
        };
    }

    /// <summary>
    /// Validate if a channel name is valid.
    /// </summary>
    public static bool IsValidChannel(string channel)
    {
        return AllChannels.Contains(channel, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the physical channel index for a channel name in 7.1 surround layout.
    /// Returns -1 if channel name is not recognized.
    /// </summary>
    /// <remarks>
    /// Standard PulseAudio channel ordering for 7.1 surround:
    /// 0=front-left, 1=front-right, 2=front-center, 3=lfe,
    /// 4=rear-left, 5=rear-right, 6=side-left, 7=side-right
    /// </remarks>
    public static int GetChannelIndex(string channelName) => channelName.ToLowerInvariant() switch
    {
        "front-left" => 0,
        "front-right" => 1,
        "front-center" => 2,
        "lfe" => 3,
        "rear-left" => 4,
        "rear-right" => 5,
        "side-left" => 6,
        "side-right" => 7,
        "mono" => 0,
        _ => -1
    };
}
