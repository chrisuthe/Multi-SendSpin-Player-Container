namespace MultiRoomAudio.Models.PlayerStats;

/// <summary>
/// Audio format information for input, output, and hardware sink.
/// </summary>
public record AudioFormatStats(
    string InputFormat,
    int InputSampleRate,
    int InputChannels,
    string? InputBitrate,
    string OutputFormat,
    int OutputSampleRate,
    int OutputChannels,
    int OutputBitDepth,
    // Hardware sink format (what PulseAudio negotiated with the device)
    string? HardwareFormat = null,      // e.g., "S32LE", "S24LE", "FLOAT32LE"
    int? HardwareSampleRate = null,     // Actual sink sample rate
    int? HardwareBitDepth = null        // Derived bit depth (16, 24, or 32)
);
