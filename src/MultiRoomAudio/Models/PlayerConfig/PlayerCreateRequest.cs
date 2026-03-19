using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Request to create a new player.
/// </summary>
public class PlayerCreateRequest
{
    /// <summary>
    /// The name of the player. Must be 1-100 characters and not contain control characters.
    /// Supports international characters, symbols, and special characters.
    /// </summary>
    [Required(ErrorMessage = "Player name is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Player name must be between 1 and 100 characters.")]
    [RegularExpression(@"^[^\x00-\x1F\x7F]+$", ErrorMessage = "Player name cannot contain control characters.")]
    public required string Name { get; set; }

    /// <summary>
    /// The audio device to use (optional, uses default if not specified).
    /// </summary>
    [StringLength(100, ErrorMessage = "Device name cannot exceed 100 characters.")]
    public string? Device { get; set; }

    /// <summary>
    /// Optional client ID. If not provided, one will be generated from the name.
    /// </summary>
    [StringLength(64, ErrorMessage = "ClientId cannot exceed 64 characters.")]
    public string? ClientId { get; set; }

    /// <summary>
    /// The server URL to connect to (optional, uses mDNS discovery if not specified).
    /// </summary>
    [Url(ErrorMessage = "ServerUrl must be a valid URL.")]
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Volume level from 0 to 100.
    /// </summary>
    [Range(0, 100, ErrorMessage = "Volume must be between 0 and 100.")]
    public int Volume { get; set; } = 75;

    /// <summary>
    /// Audio delay offset in milliseconds. Can be negative or positive.
    /// </summary>
    [Range(-10000, 10000, ErrorMessage = "DelayMs must be between -10000 and 10000 milliseconds.")]
    public int DelayMs { get; set; }

    /// <summary>
    /// Logging level for this player.
    /// </summary>
    public string LogLevel { get; set; } = "INFO";

    /// <summary>
    /// Audio codec to use.
    /// </summary>
    public string Codec { get; set; } = "opus";

    /// <summary>
    /// Buffer size in milliseconds.
    /// </summary>
    [Range(10, 10000, ErrorMessage = "BufferSizeMs must be between 10 and 10000 milliseconds.")]
    public int BufferSizeMs { get; set; } = 100;

    /// <summary>
    /// Whether to persist the player configuration to disk.
    /// Persisted players will autostart on next launch.
    /// </summary>
    public bool Persist { get; set; } = true;

    /// <summary>
    /// Specific audio format to advertise. If null or empty, defaults to "flac-48000" for maximum MA compatibility.
    /// Format string: "codec-samplerate-bitdepth" (e.g., "flac-192000", "pcm-96000-24").
    /// UI selection only available when ENABLE_ADVANCED_FORMATS is enabled.
    /// </summary>
    [StringLength(50, ErrorMessage = "AdvertisedFormat cannot exceed 50 characters.")]
    public string? AdvertisedFormat { get; set; }
}
