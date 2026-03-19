using MultiRoomAudio.Models.SinkModels;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// Represents a sink detected in default.pa.
/// </summary>
/// <param name="LineNumber">1-based line number where the entry starts.</param>
/// <param name="EndLineNumber">1-based line number where the entry ends (same as LineNumber for single-line entries).</param>
/// <param name="RawLine">The complete line content (with continuations merged).</param>
/// <param name="Type">Type of sink (Combine or Remap).</param>
/// <param name="SinkName">The sink_name parameter value.</param>
/// <param name="Description">Optional description from sink_properties.</param>
/// <param name="Slaves">List of slave sinks (for combine-sink).</param>
/// <param name="MasterSink">Master sink name (for remap-sink).</param>
/// <param name="Channels">Number of channels (for remap-sink).</param>
/// <param name="ChannelMap">Output channel map (for remap-sink).</param>
/// <param name="MasterChannelMap">Master channel map (for remap-sink).</param>
/// <param name="Remix">Remix setting (for remap-sink).</param>
public record DetectedSink(
    int LineNumber,
    int EndLineNumber,
    string RawLine,
    CustomSinkType Type,
    string SinkName,
    string? Description,
    // Combine-specific
    List<string>? Slaves,
    // Remap-specific
    string? MasterSink,
    int? Channels,
    string? ChannelMap,
    string? MasterChannelMap,
    bool? Remix
);
