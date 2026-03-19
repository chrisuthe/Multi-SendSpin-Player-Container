using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.SinkModels;

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
