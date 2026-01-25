using MultiRoomAudio.Services;
using MultiRoomAudio.Utilities;

namespace MultiRoomAudio.Audio.Mock;

/// <summary>
/// Mock PulseAudio module runner for testing without real PulseAudio.
/// Simulates module loading/unloading operations in memory.
/// </summary>
public class MockPaModuleRunner : IPaModuleRunner
{
    private readonly ILogger<MockPaModuleRunner> _logger;
    private readonly MockHardwareConfigService? _configService;
    private readonly Dictionary<int, MockModule> _modules = new();
    private readonly Dictionary<string, int> _sinkToModule = new(StringComparer.OrdinalIgnoreCase);
    private int _nextModuleIndex = 100;

    private record MockModule(int Index, string Type, string SinkName, string? Description);

    public MockPaModuleRunner(
        ILogger<MockPaModuleRunner> logger,
        MockHardwareConfigService? configService = null)
    {
        _logger = logger;
        _configService = configService;
        _logger.LogInformation("Mock PaModuleRunner initialized");
    }

    public Task<int?> LoadCombineSinkAsync(
        string sinkName,
        IEnumerable<string> slaves,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (!PaModuleRunner.ValidateName(sinkName, out var error))
        {
            _logger.LogWarning("Invalid sink name rejected: {Error}", error);
            throw new ArgumentException(error, nameof(sinkName));
        }

        if (_sinkToModule.ContainsKey(sinkName))
        {
            _logger.LogWarning("Mock combine-sink '{SinkName}' already exists", sinkName);
            return Task.FromResult<int?>(null);
        }

        var slaveList = slaves.ToList();
        var moduleIndex = _nextModuleIndex++;
        var module = new MockModule(moduleIndex, "module-combine-sink", sinkName, description);

        _modules[moduleIndex] = module;
        _sinkToModule[sinkName] = moduleIndex;

        _logger.LogInformation(
            "Mock: Loaded combine-sink '{SinkName}' with {SlaveCount} slaves, module index {Index}",
            sinkName, slaveList.Count, moduleIndex);

        return Task.FromResult<int?>(moduleIndex);
    }

    public Task<int?> LoadRemapSinkAsync(
        string sinkName,
        string masterSink,
        int channels,
        string channelMap,
        string masterChannelMap,
        bool remix = false,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (!PaModuleRunner.ValidateName(sinkName, out var error))
        {
            _logger.LogWarning("Invalid sink name rejected: {Error}", error);
            throw new ArgumentException(error, nameof(sinkName));
        }

        if (_sinkToModule.ContainsKey(sinkName))
        {
            _logger.LogWarning("Mock remap-sink '{SinkName}' already exists", sinkName);
            return Task.FromResult<int?>(null);
        }

        var moduleIndex = _nextModuleIndex++;
        var module = new MockModule(moduleIndex, "module-remap-sink", sinkName, description);

        _modules[moduleIndex] = module;
        _sinkToModule[sinkName] = moduleIndex;

        _logger.LogInformation(
            "Mock: Loaded remap-sink '{SinkName}' from master '{Master}' with {Channels} channels, module index {Index}",
            sinkName, masterSink, channels, moduleIndex);

        return Task.FromResult<int?>(moduleIndex);
    }

    public Task<bool> UnloadModuleAsync(int moduleIndex, CancellationToken cancellationToken = default)
    {
        if (!_modules.TryGetValue(moduleIndex, out var module))
        {
            _logger.LogWarning("Mock: Module {Index} not found", moduleIndex);
            return Task.FromResult(false);
        }

        _modules.Remove(moduleIndex);
        _sinkToModule.Remove(module.SinkName);

        _logger.LogInformation("Mock: Unloaded module {Index} (sink '{SinkName}')", moduleIndex, module.SinkName);
        return Task.FromResult(true);
    }

    public Task<bool> IsModuleLoadedAsync(int moduleIndex, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_modules.ContainsKey(moduleIndex));
    }

    public Task<IReadOnlyList<PaModule>> ListModulesAsync(CancellationToken cancellationToken = default)
    {
        var modules = _modules.Values
            .Select(m => new PaModule(m.Index, m.Type, $"sink_name={m.SinkName}"))
            .ToList();

        return Task.FromResult<IReadOnlyList<PaModule>>(modules);
    }

    public Task<bool> SinkExistsAsync(string sinkName, CancellationToken cancellationToken = default)
    {
        if (!PaModuleRunner.ValidateName(sinkName, out _))
            return Task.FromResult(false);

        // Check custom sinks first
        if (_sinkToModule.ContainsKey(sinkName))
            return Task.FromResult(true);

        // Check hardware sinks from mock devices
        var devices = _configService?.GetEnabledAudioDevices()
            ?? new List<Models.MockAudioDeviceConfig>();
        return Task.FromResult(devices.Any(d =>
            d.Id.Equals(sinkName, StringComparison.OrdinalIgnoreCase)));
    }
}
