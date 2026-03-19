using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request to update a player's configuration.
/// All fields are optional - only provided fields will be updated.
/// </summary>
public class PlayerUpdateRequest
{
    /// <summary>
    /// New name for the player. If provided, the player will be renamed.
    /// </summary>
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Player name must be between 1 and 100 characters.")]
    [RegularExpression(@"^[^\x00-\x1F\x7F]+$", ErrorMessage = "Player name cannot contain control characters.")]
    public string? Name { get; set; }

    /// <summary>
    /// The audio device to use. Set to empty string for default device.
    /// </summary>
    [StringLength(100, ErrorMessage = "Device name cannot exceed 100 characters.")]
    public string? Device { get; set; }

    /// <summary>
    /// The server URL to connect to. Set to empty string for mDNS discovery.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Volume level from 0 to 100.
    /// </summary>
    [Range(0, 100, ErrorMessage = "Volume must be between 0 and 100.")]
    public int? Volume { get; set; }

    /// <summary>
    /// Buffer size in milliseconds.
    /// </summary>
    [Range(10, 10000, ErrorMessage = "BufferSizeMs must be between 10 and 10000 milliseconds.")]
    public int? BufferSizeMs { get; set; }

    /// <summary>
    /// Specific audio format to advertise. If null or empty, defaults to "flac-48000" for maximum MA compatibility.
    /// Format string: "codec-samplerate-bitdepth" (e.g., "flac-192000", "pcm-96000-24").
    /// UI selection only available when ENABLE_ADVANCED_FORMATS is enabled.
    /// </summary>
    [StringLength(50, ErrorMessage = "AdvertisedFormat cannot exceed 50 characters.")]
    public string? AdvertisedFormat { get; set; }
}
