using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// Request to create a remap-sink (channel extraction).
/// </summary>
public class RemapSinkCreateRequest
{
    /// <summary>
    /// Unique name for the sink.
    /// Must contain only letters, numbers, underscores, hyphens, and dots.
    /// </summary>
    [Required(ErrorMessage = "Sink name is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Sink name must be 1-100 characters.")]
    [RegularExpression(@"^[a-zA-Z0-9_\-\.]+$", ErrorMessage = "Sink name can only contain letters, numbers, underscores, hyphens, and dots.")]
    public required string Name { get; set; }

    /// <summary>
    /// Human-readable description for the sink.
    /// Supports spaces, ampersands, and other special characters.
    /// </summary>
    [StringLength(200, ErrorMessage = "Description must be 200 characters or less.")]
    public string? Description { get; set; }

    /// <summary>
    /// Master sink to extract channels from.
    /// </summary>
    [Required(ErrorMessage = "Master sink is required.")]
    public required string MasterSink { get; set; }

    /// <summary>
    /// Number of output channels (typically 2 for stereo).
    /// </summary>
    [Range(1, 8, ErrorMessage = "Channels must be between 1 and 8.")]
    public int Channels { get; set; } = 2;

    /// <summary>
    /// Channel mappings defining how output channels map to master channels.
    /// </summary>
    [Required(ErrorMessage = "At least one channel mapping is required.")]
    [MinLength(1, ErrorMessage = "At least one channel mapping is required.")]
    public required List<ChannelMapping> ChannelMappings { get; set; }

    /// <summary>
    /// Whether to remix (false = no mixing, just routing).
    /// </summary>
    public bool Remix { get; set; } = false;
}
