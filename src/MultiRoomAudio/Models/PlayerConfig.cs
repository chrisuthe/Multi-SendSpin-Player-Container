using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models;

/// <summary>
/// Request to create a new player.
/// </summary>
public class PlayerCreateRequest
{
    /// <summary>
    /// The name of the player. Must be 1-100 characters and contain only
    /// letters, numbers, spaces, hyphens, underscores, and apostrophes.
    /// </summary>
    [Required(ErrorMessage = "Player name is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Player name must be between 1 and 100 characters.")]
    [RegularExpression(@"^[a-zA-Z0-9\s\-_']+$", ErrorMessage = "Player name can only contain letters, numbers, spaces, hyphens, underscores, and apostrophes.")]
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
}

/// <summary>
/// Request to switch audio device.
/// </summary>
/// <param name="Device">The device identifier to switch to. If null, the default device will be used.</param>
public record DeviceSwitchRequest(
    [property: StringLength(100, ErrorMessage = "Device name cannot exceed 100 characters.")]
    string? Device);

/// <summary>
/// Request to set mute state.
/// </summary>
/// <param name="Muted">True to mute the player, false to unmute.</param>
public record MuteRequest(bool Muted);

/// <summary>
/// Request to set volume.
/// </summary>
/// <param name="Volume">Volume level from 0 to 100.</param>
public record VolumeRequest(
    [property: Range(0, 100, ErrorMessage = "Volume must be between 0 and 100.")]
    int Volume);

/// <summary>
/// Request to update offset.
/// </summary>
/// <param name="DelayMs">Audio delay offset in milliseconds (-10000 to 10000).</param>
public record OffsetRequest(
    [property: Range(-10000, 10000, ErrorMessage = "DelayMs must be between -10000 and 10000 milliseconds.")]
    int DelayMs);

/// <summary>
/// Request to rename a player.
/// </summary>
/// <param name="NewName">The new name for the player.</param>
public record RenameRequest(
    [property: Required(ErrorMessage = "New player name is required.")]
    [property: StringLength(100, MinimumLength = 1, ErrorMessage = "Player name must be between 1 and 100 characters.")]
    [property: RegularExpression(@"^[a-zA-Z0-9\s\-_']+$", ErrorMessage = "Player name can only contain letters, numbers, spaces, hyphens, underscores, and apostrophes.")]
    string NewName);

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
    [RegularExpression(@"^[a-zA-Z0-9\s\-_']+$", ErrorMessage = "Player name can only contain letters, numbers, spaces, hyphens, underscores, and apostrophes.")]
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
}

/// <summary>
/// Player configuration stored in memory.
/// </summary>
public class PlayerConfig
{
    public required string Name { get; set; }
    public required string ClientId { get; set; }
    public string? DeviceId { get; set; }
    public string? ServerUrl { get; set; }
    public int DelayMs { get; set; }
    public string LogLevel { get; set; } = "INFO";
    public string Codec { get; set; } = "opus";
    public int BufferSizeMs { get; set; } = 100;
    public int Volume { get; set; } = 75;
    public bool IsMuted { get; set; }
}
