using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models;

/// <summary>
/// Type of custom PulseAudio sink.
/// </summary>
public enum CustomSinkType
{
    /// <summary>module-combine-sink - Merge multiple outputs.</summary>
    Combine,
    /// <summary>module-remap-sink - Channel extraction/remapping.</summary>
    Remap
}

/// <summary>
/// State of a custom sink.
/// </summary>
public enum CustomSinkState
{
    /// <summary>Configuration created but not yet loaded.</summary>
    Created,
    /// <summary>Currently loading the module.</summary>
    Loading,
    /// <summary>Module loaded successfully in PulseAudio.</summary>
    Loaded,
    /// <summary>Failed to load or encountered an error.</summary>
    Error,
    /// <summary>Currently unloading the module.</summary>
    Unloading
}

/// <summary>
/// Channel mapping for remap-sink.
/// Defines how a single output channel maps to a source channel from the master sink.
/// </summary>
public class ChannelMapping
{
    /// <summary>
    /// Output channel name (e.g., "front-left", "front-right").
    /// This is the channel in the virtual sink.
    /// </summary>
    [Required]
    public required string OutputChannel { get; set; }

    /// <summary>
    /// Source channel from master sink.
    /// This is the channel to read from the physical device.
    /// </summary>
    [Required]
    public required string MasterChannel { get; set; }
}

/// <summary>
/// Request to create a combine-sink (merge multiple outputs).
/// </summary>
public class CombineSinkCreateRequest
{
    /// <summary>
    /// Unique name for the sink. Will be used as sink_name parameter.
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
    /// List of slave sink names/IDs to combine.
    /// Must contain at least 2 sinks.
    /// </summary>
    [Required(ErrorMessage = "At least 2 slave sinks are required.")]
    [MinLength(2, ErrorMessage = "At least 2 slave sinks are required.")]
    public required List<string> Slaves { get; set; }
}

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

/// <summary>
/// Request to import sinks from default.pa.
/// </summary>
public class ImportSinksRequest
{
    /// <summary>
    /// Line numbers of sinks to import from default.pa.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one line number is required.")]
    public required List<int> LineNumbers { get; set; }
}

/// <summary>
/// Configuration for a custom sink (stored in YAML).
/// </summary>
public class CustomSinkConfiguration
{
    /// <summary>
    /// Unique name for the sink.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Type of sink (Combine or Remap).
    /// </summary>
    public CustomSinkType Type { get; set; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string? Description { get; set; }

    // Combine-sink specific properties

    /// <summary>
    /// List of slave sink names (for combine-sink).
    /// </summary>
    public List<string>? Slaves { get; set; }

    // Remap-sink specific properties

    /// <summary>
    /// Master sink name (for remap-sink).
    /// </summary>
    public string? MasterSink { get; set; }

    /// <summary>
    /// Number of output channels (for remap-sink).
    /// </summary>
    public int Channels { get; set; } = 2;

    /// <summary>
    /// Channel mappings (for remap-sink).
    /// </summary>
    public List<ChannelMapping>? ChannelMappings { get; set; }

    /// <summary>
    /// Whether to remix (for remap-sink).
    /// </summary>
    public bool Remix { get; set; } = false;
}

/// <summary>
/// Response for a custom sink.
/// </summary>
public record CustomSinkResponse(
    string Name,
    CustomSinkType Type,
    CustomSinkState State,
    string? Description,
    int? ModuleIndex,
    string? PulseAudioSinkName,
    string? ErrorMessage,
    DateTime CreatedAt,
    // Combine-sink specific
    List<string>? Slaves = null,
    // Remap-sink specific
    string? MasterSink = null,
    int? Channels = null,
    List<ChannelMapping>? ChannelMappings = null
);

/// <summary>
/// List response for custom sinks.
/// </summary>
public record CustomSinksListResponse(
    List<CustomSinkResponse> Sinks,
    int Count
);

/// <summary>
/// Response for import scan results.
/// </summary>
public record ImportScanResponse(
    int Found,
    List<DetectedSinkInfo> Sinks
);

/// <summary>
/// Information about a detected sink in default.pa.
/// </summary>
public record DetectedSinkInfo(
    int LineNumber,
    string Type,
    string Name,
    string? Description,
    List<string>? Slaves,
    string? MasterSink,
    string Preview
);

/// <summary>
/// Response for import operation.
/// </summary>
public record ImportResultResponse(
    List<string> Imported,
    List<string> Errors
);

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
