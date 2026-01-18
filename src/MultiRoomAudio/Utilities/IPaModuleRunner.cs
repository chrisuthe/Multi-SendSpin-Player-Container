namespace MultiRoomAudio.Utilities;

/// <summary>
/// Interface for PulseAudio module runner operations.
/// Allows mock implementations for testing.
/// </summary>
public interface IPaModuleRunner
{
    Task<int?> LoadCombineSinkAsync(
        string sinkName,
        IEnumerable<string> slaves,
        string? description = null,
        CancellationToken cancellationToken = default);

    Task<int?> LoadRemapSinkAsync(
        string sinkName,
        string masterSink,
        int channels,
        string channelMap,
        string masterChannelMap,
        bool remix = false,
        string? description = null,
        CancellationToken cancellationToken = default);

    Task<bool> UnloadModuleAsync(int moduleIndex, CancellationToken cancellationToken = default);

    Task<bool> IsModuleLoadedAsync(int moduleIndex, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaModule>> ListModulesAsync(CancellationToken cancellationToken = default);

    Task<bool> SinkExistsAsync(string sinkName, CancellationToken cancellationToken = default);
}
