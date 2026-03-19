namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Audio device information.
/// </summary>
public record AudioDevice(
    int Index,
    string Id,
    string Name,
    int MaxChannels,
    int DefaultSampleRate,
    int DefaultLowLatencyMs,
    int DefaultHighLatencyMs,
    bool IsDefault,
    DeviceCapabilities? Capabilities = null,
    DeviceIdentifiers? Identifiers = null,
    string? Alias = null,
    bool Hidden = false,
    string[]? ChannelMap = null,  // Channel names in device order, e.g., ["front-left", "front-right", "rear-left", ...]
    string? SampleFormat = null,  // PulseAudio sample format, e.g., "s16le", "s24le", "s32le", "float32le"
    int? CardIndex = null,        // ALSA card number this device belongs to (from alsa.card or device.card property, NOT PulseAudio card index)
    string? SinkType = null,      // null for hardware devices, "Combine" or "Remap" for custom sinks
    CapabilitySource? CapabilitySource = null,  // Where capability data came from: Alsa (hardware) or PulseAudioMax (inferred)
    bool IsOffProfile = false,    // True if card exists but has "off" profile - device will work when profile is activated
    string? CardName = null       // PulseAudio card name (for off-profile devices to enable profile activation)
)
{
    /// <summary>
    /// Gets the bit depth derived from the PulseAudio sample format string.
    /// </summary>
    public int? BitDepth => GetBitDepthFromFormat(SampleFormat);

    /// <summary>
    /// Derives bit depth from PulseAudio sample format string.
    /// </summary>
    private static int? GetBitDepthFromFormat(string? format)
    {
        if (string.IsNullOrEmpty(format))
            return null;

        return format.ToLowerInvariant() switch
        {
            "s16le" or "s16be" or "u8" => 16,
            "s24le" or "s24be" or "s24-32le" or "s24-32be" => 24,
            "s32le" or "s32be" or "float32le" or "float32be" => 32,
            _ => null
        };
    }
};
