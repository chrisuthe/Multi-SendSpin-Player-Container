using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.SinkModels;

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
