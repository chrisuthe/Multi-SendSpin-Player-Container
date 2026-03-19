namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Detailed device capabilities including supported sample rates and bit depths.
/// </summary>
public record DeviceCapabilities(
    int[] SupportedSampleRates,
    int[] SupportedBitDepths,
    int MaxChannels,
    int PreferredSampleRate,
    int PreferredBitDepth
);
