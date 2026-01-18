namespace MultiRoomAudio.Models;

// =============================================================================
// API Response Models
// =============================================================================
// This file contains lightweight API response types used across multiple endpoints.
// These types are grouped together because:
// 1. They are all simple record types with minimal logic
// 2. They serve a common purpose: API serialization
// 3. Splitting into separate files would create unnecessary file proliferation
//    for types that are each under 10 lines
//
// Device-specific types: AudioDevice, DevicesListResponse, DeviceCapabilities
// Audio format types: AudioOutputFormat
// Generic response types: ErrorResponse, SuccessResponse, HealthResponse
// =============================================================================

/// <summary>
/// Stable device identifiers extracted from PulseAudio properties.
/// Used for re-matching devices across reboots when sink names change.
/// </summary>
public record DeviceIdentifiers(
    string? Serial,           // device.serial - most stable if device supports it
    string? BusPath,          // device.bus_path - stable per USB port
    string? VendorId,         // device.vendor.id
    string? ProductId,        // device.product.id
    string? AlsaLongCardName  // alsa.long_card_name - includes USB path info
);

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
    bool Hidden = false
);

/// <summary>
/// Response containing device list.
/// </summary>
public record DevicesListResponse(
    List<AudioDevice> Devices,
    int Count
);

/// <summary>
/// Error response format.
/// </summary>
public record ErrorResponse(
    bool Success,
    string Message
);

/// <summary>
/// Success response format.
/// </summary>
public record SuccessResponse(
    bool Success,
    string Message
);

/// <summary>
/// Response for player rename operations.
/// Indicates whether restart is required for changes to propagate.
/// </summary>
public record PlayerRenameResponse(
    bool Success,
    string Message,
    string NewName,
    bool RestartRequired = true,
    string? RestartHint = "Restart the player for the name change to appear in Music Assistant."
);

/// <summary>
/// Health check response.
/// </summary>
public record HealthResponse(
    string Status,
    DateTime Timestamp,
    string Version
);
