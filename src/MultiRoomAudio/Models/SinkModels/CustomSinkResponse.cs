namespace MultiRoomAudio.Models.SinkModels;

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
