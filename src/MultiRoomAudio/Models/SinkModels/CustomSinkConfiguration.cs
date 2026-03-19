namespace MultiRoomAudio.Models.SinkModels;

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
    /// May become stale after reboot if ALSA card numbers change.
    /// Use SlaveIdentifiers for stable re-matching.
    /// </summary>
    public List<string>? Slaves { get; set; }

    /// <summary>
    /// Stable identifiers for slave sinks (for combine-sink).
    /// Used to re-match slaves when ALSA card numbers change after reboot.
    /// The list order matches Slaves.
    /// </summary>
    public List<SinkIdentifiersConfig>? SlaveIdentifiers { get; set; }

    // Remap-sink specific properties

    /// <summary>
    /// Master sink name (for remap-sink).
    /// May become stale after reboot if ALSA card numbers change.
    /// Use MasterSinkIdentifiers for stable re-matching.
    /// </summary>
    public string? MasterSink { get; set; }

    /// <summary>
    /// Stable identifiers for the master sink (for remap-sink).
    /// Used to re-match the master sink when ALSA card numbers change after reboot.
    /// </summary>
    public SinkIdentifiersConfig? MasterSinkIdentifiers { get; set; }

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
