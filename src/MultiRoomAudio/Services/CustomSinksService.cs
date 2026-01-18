using System.Collections.Concurrent;
using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages custom PulseAudio sinks (combine-sink and remap-sink).
/// Implements IHostedService for startup sink creation and shutdown cleanup.
/// </summary>
public class CustomSinksService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<CustomSinksService> _logger;
    private readonly IPaModuleRunner _moduleRunner;
    private readonly EnvironmentService _environment;
    private readonly ConcurrentDictionary<string, CustomSinkContext> _sinks = new();
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly ReaderWriterLockSlim _configLock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    /// <summary>
    /// Internal context for tracking sink state.
    /// </summary>
    private record CustomSinkContext(
        CustomSinkConfiguration Config,
        DateTime CreatedAt
    )
    {
        public CustomSinkState State { get; set; } = CustomSinkState.Created;
        public int? ModuleIndex { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public CustomSinksService(
        ILogger<CustomSinksService> logger,
        IPaModuleRunner moduleRunner,
        EnvironmentService environment)
    {
        _logger = logger;
        _moduleRunner = moduleRunner;
        _environment = environment;
        _configPath = Path.Combine(environment.ConfigPath, "custom-sinks.yaml");

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <summary>
    /// Load and start persisted sinks on startup.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CustomSinksService starting...");

        // Load configurations from YAML
        var configs = LoadConfigurations();

        if (configs.Count == 0)
        {
            _logger.LogInformation("No custom sinks configured");
            return;
        }

        // Sort by dependency order: combine sinks first, then remap sinks
        // (remap sinks might depend on combine sinks as their master)
        var sorted = configs
            .OrderBy(c => c.Type == CustomSinkType.Combine ? 0 : 1)
            .ToList();

        var loadedCount = 0;
        var failedCount = 0;

        foreach (var config in sorted)
        {
            try
            {
                await LoadSinkAsync(config, cancellationToken);
                loadedCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "Failed to load sink '{Name}' on startup", config.Name);
            }
        }

        if (failedCount > 0)
        {
            _logger.LogWarning("CustomSinksService started: {Loaded} sinks loaded, {Failed} failed",
                loadedCount, failedCount);
        }
        else
        {
            _logger.LogInformation("CustomSinksService started with {Count} sinks loaded", loadedCount);
        }
    }

    /// <summary>
    /// Unload all sinks on shutdown.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CustomSinksService stopping...");

        foreach (var (name, context) in _sinks)
        {
            if (context.ModuleIndex.HasValue)
            {
                try
                {
                    await _moduleRunner.UnloadModuleAsync(context.ModuleIndex.Value, cancellationToken);
                    _logger.LogDebug("Unloaded sink '{Name}' (module {Index})", name, context.ModuleIndex.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to unload sink '{Name}'", name);
                }
            }
        }

        _sinks.Clear();
        _logger.LogInformation("CustomSinksService stopped");
    }

    /// <summary>
    /// Create a new combine-sink.
    /// </summary>
    public async Task<CustomSinkResponse> CreateCombineSinkAsync(
        CombineSinkCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSinkName(request.Name);

        // Check if sink name already exists in PulseAudio before we try to add locally
        if (await _moduleRunner.SinkExistsAsync(request.Name, cancellationToken))
        {
            throw new InvalidOperationException($"A PulseAudio sink with name '{request.Name}' already exists.");
        }

        var config = new CustomSinkConfiguration
        {
            Name = request.Name,
            Type = CustomSinkType.Combine,
            Description = request.Description,
            Slaves = request.Slaves
        };

        var context = new CustomSinkContext(config, DateTime.UtcNow)
        {
            State = CustomSinkState.Loading
        };

        // TryAdd is atomic - if another thread added the same name, we fail gracefully
        if (!_sinks.TryAdd(request.Name, context))
        {
            throw new InvalidOperationException($"Sink '{request.Name}' already exists.");
        }

        // Use try-finally to ensure cleanup on any failure path
        bool success = false;
        try
        {
            var moduleIndex = await _moduleRunner.LoadCombineSinkAsync(
                request.Name,
                request.Slaves,
                request.Description,
                cancellationToken);

            if (moduleIndex.HasValue)
            {
                context.ModuleIndex = moduleIndex.Value;
                context.State = CustomSinkState.Loaded;
                _logger.LogInformation("Created combine-sink '{Name}' with module index {Index}",
                    request.Name, moduleIndex.Value);

                // Persist to YAML only on success
                SaveConfiguration(config);
                success = true;

                return ToResponse(request.Name, context);
            }
            else
            {
                throw new InvalidOperationException($"Failed to load combine-sink '{request.Name}' in PulseAudio");
            }
        }
        finally
        {
            if (!success)
            {
                // Ensure we clean up on any failure - unload module if it was loaded
                if (context.ModuleIndex.HasValue)
                {
                    try
                    {
                        await _moduleRunner.UnloadModuleAsync(context.ModuleIndex.Value, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to unload module during cleanup for sink '{Name}'", request.Name);
                    }
                }

                context.State = CustomSinkState.Error;
                context.ErrorMessage ??= "Creation failed";
                _sinks.TryRemove(request.Name, out _);
            }
        }
    }

    /// <summary>
    /// Create a new remap-sink.
    /// </summary>
    public async Task<CustomSinkResponse> CreateRemapSinkAsync(
        RemapSinkCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSinkName(request.Name);

        // Validate master sink exists
        if (!await _moduleRunner.SinkExistsAsync(request.MasterSink, cancellationToken))
        {
            throw new ArgumentException($"Master sink '{request.MasterSink}' not found. Use /api/devices to list available sinks.");
        }

        // Check if sink name already exists in PulseAudio before we try to add locally
        if (await _moduleRunner.SinkExistsAsync(request.Name, cancellationToken))
        {
            throw new InvalidOperationException($"A PulseAudio sink with name '{request.Name}' already exists.");
        }

        // Build channel maps
        var channelMap = string.Join(",", request.ChannelMappings.Select(m => m.OutputChannel));
        var masterChannelMap = string.Join(",", request.ChannelMappings.Select(m => m.MasterChannel));

        var config = new CustomSinkConfiguration
        {
            Name = request.Name,
            Type = CustomSinkType.Remap,
            Description = request.Description,
            MasterSink = request.MasterSink,
            Channels = request.Channels,
            ChannelMappings = request.ChannelMappings,
            Remix = request.Remix
        };

        var context = new CustomSinkContext(config, DateTime.UtcNow)
        {
            State = CustomSinkState.Loading
        };

        // TryAdd is atomic - if another thread added the same name, we fail gracefully
        if (!_sinks.TryAdd(request.Name, context))
        {
            throw new InvalidOperationException($"Sink '{request.Name}' already exists.");
        }

        // Use try-finally to ensure cleanup on any failure path
        bool success = false;
        try
        {
            var moduleIndex = await _moduleRunner.LoadRemapSinkAsync(
                request.Name,
                request.MasterSink,
                request.Channels,
                channelMap,
                masterChannelMap,
                request.Remix,
                request.Description,
                cancellationToken);

            if (moduleIndex.HasValue)
            {
                context.ModuleIndex = moduleIndex.Value;
                context.State = CustomSinkState.Loaded;
                _logger.LogInformation("Created remap-sink '{Name}' with module index {Index}",
                    request.Name, moduleIndex.Value);

                // Persist to YAML only on success
                SaveConfiguration(config);
                success = true;

                return ToResponse(request.Name, context);
            }
            else
            {
                throw new InvalidOperationException($"Failed to load remap-sink '{request.Name}' in PulseAudio");
            }
        }
        finally
        {
            if (!success)
            {
                // Ensure we clean up on any failure - unload module if it was loaded
                if (context.ModuleIndex.HasValue)
                {
                    try
                    {
                        await _moduleRunner.UnloadModuleAsync(context.ModuleIndex.Value, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to unload module during cleanup for sink '{Name}'", request.Name);
                    }
                }

                context.State = CustomSinkState.Error;
                context.ErrorMessage ??= "Creation failed";
                _sinks.TryRemove(request.Name, out _);
            }
        }
    }

    /// <summary>
    /// Import a sink detected from default.pa.
    /// </summary>
    public async Task<CustomSinkResponse> ImportSinkAsync(
        DetectedSink detected,
        CancellationToken cancellationToken = default)
    {
        ValidateSinkName(detected.SinkName);

        if (_sinks.ContainsKey(detected.SinkName))
        {
            throw new InvalidOperationException($"Sink '{detected.SinkName}' already exists in app management.");
        }

        CustomSinkConfiguration config;

        if (detected.Type == CustomSinkType.Combine)
        {
            config = new CustomSinkConfiguration
            {
                Name = detected.SinkName,
                Type = CustomSinkType.Combine,
                Description = detected.Description,
                Slaves = detected.Slaves ?? []
            };
        }
        else
        {
            // Parse channel mappings from the detected sink
            var channelMappings = new List<ChannelMapping>();
            if (!string.IsNullOrEmpty(detected.ChannelMap) && !string.IsNullOrEmpty(detected.MasterChannelMap))
            {
                var outputChannels = detected.ChannelMap.Split(',');
                var masterChannels = detected.MasterChannelMap.Split(',');
                for (int i = 0; i < Math.Min(outputChannels.Length, masterChannels.Length); i++)
                {
                    channelMappings.Add(new ChannelMapping
                    {
                        OutputChannel = outputChannels[i].Trim(),
                        MasterChannel = masterChannels[i].Trim()
                    });
                }
            }

            config = new CustomSinkConfiguration
            {
                Name = detected.SinkName,
                Type = CustomSinkType.Remap,
                Description = detected.Description,
                MasterSink = detected.MasterSink,
                Channels = detected.Channels ?? 2,
                ChannelMappings = channelMappings,
                Remix = detected.Remix ?? false
            };
        }

        var context = new CustomSinkContext(config, DateTime.UtcNow)
        {
            // Mark as loaded since the sink is already loaded by PulseAudio from default.pa
            State = CustomSinkState.Loaded
        };

        _sinks[detected.SinkName] = context;

        // Persist to YAML
        SaveConfiguration(config);

        _logger.LogInformation("Imported {Type}-sink '{Name}' from default.pa",
            detected.Type, detected.SinkName);

        return ToResponse(detected.SinkName, context);
    }

    /// <summary>
    /// Delete a custom sink.
    /// </summary>
    public async Task<bool> DeleteSinkAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_sinks.TryRemove(name, out var context))
        {
            return false;
        }

        // Unload module if loaded
        if (context.ModuleIndex.HasValue)
        {
            context.State = CustomSinkState.Unloading;
            try
            {
                await _moduleRunner.UnloadModuleAsync(context.ModuleIndex.Value, cancellationToken);
                _logger.LogInformation("Unloaded sink '{Name}' (module {Index})", name, context.ModuleIndex.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unload module for sink '{Name}'", name);
            }
        }

        // Remove from YAML
        RemoveConfiguration(name);

        _logger.LogInformation("Deleted sink '{Name}'", name);
        return true;
    }

    /// <summary>
    /// Get a sink by name.
    /// </summary>
    public CustomSinkResponse? GetSink(string name)
    {
        if (!_sinks.TryGetValue(name, out var context))
            return null;

        return ToResponse(name, context);
    }

    /// <summary>
    /// Get all sinks.
    /// </summary>
    public CustomSinksListResponse GetAllSinks()
    {
        var sinks = _sinks
            .Select(kvp => ToResponse(kvp.Key, kvp.Value))
            .OrderBy(s => s.Name)
            .ToList();

        return new CustomSinksListResponse(sinks, sinks.Count);
    }

    /// <summary>
    /// Check if a sink is currently loaded in PulseAudio.
    /// </summary>
    public async Task<bool> IsSinkLoadedAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_sinks.TryGetValue(name, out var context))
            return false;

        if (!context.ModuleIndex.HasValue)
            return false;

        return await _moduleRunner.IsModuleLoadedAsync(context.ModuleIndex.Value, cancellationToken);
    }

    /// <summary>
    /// Reload a sink (unload and load again).
    /// </summary>
    public async Task<CustomSinkResponse?> ReloadSinkAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_sinks.TryGetValue(name, out var context))
            return null;

        // Unload if currently loaded
        if (context.ModuleIndex.HasValue)
        {
            await _moduleRunner.UnloadModuleAsync(context.ModuleIndex.Value, cancellationToken);
            context.ModuleIndex = null;
        }

        // Reload
        context.State = CustomSinkState.Loading;
        context.ErrorMessage = null;

        try
        {
            await LoadSinkAsync(context.Config, cancellationToken);
            return GetSink(name);
        }
        catch (Exception ex)
        {
            context.State = CustomSinkState.Error;
            context.ErrorMessage = ex.Message;
            return ToResponse(name, context);
        }
    }

    private async Task LoadSinkAsync(CustomSinkConfiguration config, CancellationToken cancellationToken)
    {
        var context = _sinks.GetOrAdd(config.Name, _ => new CustomSinkContext(config, DateTime.UtcNow));
        context.State = CustomSinkState.Loading;

        try
        {
            int? moduleIndex;

            if (config.Type == CustomSinkType.Combine)
            {
                moduleIndex = await _moduleRunner.LoadCombineSinkAsync(
                    config.Name,
                    config.Slaves ?? [],
                    config.Description,
                    cancellationToken);
            }
            else
            {
                var channelMap = string.Join(",", (config.ChannelMappings ?? []).Select(m => m.OutputChannel));
                var masterChannelMap = string.Join(",", (config.ChannelMappings ?? []).Select(m => m.MasterChannel));

                moduleIndex = await _moduleRunner.LoadRemapSinkAsync(
                    config.Name,
                    config.MasterSink ?? "",
                    config.Channels,
                    channelMap,
                    masterChannelMap,
                    config.Remix,
                    config.Description,
                    cancellationToken);
            }

            if (moduleIndex.HasValue)
            {
                context.ModuleIndex = moduleIndex.Value;
                context.State = CustomSinkState.Loaded;
                _logger.LogInformation("Loaded {Type}-sink '{Name}' with module index {Index}",
                    config.Type, config.Name, moduleIndex.Value);
            }
            else
            {
                // Module failed to load - set error state and throw so caller knows
                context.State = CustomSinkState.Error;
                context.ErrorMessage = "Failed to load module in PulseAudio";
                throw new InvalidOperationException($"Failed to load {config.Type}-sink '{config.Name}' in PulseAudio");
            }
        }
        catch (Exception ex)
        {
            context.State = CustomSinkState.Error;
            context.ErrorMessage = ex.Message;
            throw;
        }
    }

    private List<CustomSinkConfiguration> LoadConfigurations()
    {
        // Read file content outside the lock to avoid blocking on slow I/O
        string? yaml = null;
        bool fileExists;

        try
        {
            fileExists = File.Exists(_configPath);
            if (fileExists)
            {
                yaml = File.ReadAllText(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read custom sinks configuration from {Path}", _configPath);
            return [];
        }

        // Process the data under a read lock
        _configLock.EnterReadLock();
        try
        {
            if (!fileExists)
            {
                _logger.LogDebug("Custom sinks config not found at {Path}", _configPath);
                return [];
            }

            if (string.IsNullOrWhiteSpace(yaml))
                return [];

            var dict = _deserializer.Deserialize<Dictionary<string, CustomSinkConfiguration>>(yaml);
            if (dict == null)
                return [];

            // Ensure name field matches dictionary key
            foreach (var (name, config) in dict)
            {
                config.Name = name;
            }

            _logger.LogInformation("Loaded {Count} custom sink configurations", dict.Count);
            return dict.Values.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse custom sinks configuration from {Path}", _configPath);
            return [];
        }
        finally
        {
            _configLock.ExitReadLock();
        }
    }

    private void SaveConfiguration(CustomSinkConfiguration config)
    {
        // Read existing config file outside the lock
        string? existingYaml = null;
        try
        {
            if (File.Exists(_configPath))
            {
                existingYaml = File.ReadAllText(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read existing config");
        }

        // Process and serialize under write lock
        string yamlToWrite;
        _configLock.EnterWriteLock();
        try
        {
            var configs = new Dictionary<string, CustomSinkConfiguration>();

            if (!string.IsNullOrWhiteSpace(existingYaml))
            {
                try
                {
                    configs = _deserializer.Deserialize<Dictionary<string, CustomSinkConfiguration>>(existingYaml)
                        ?? new Dictionary<string, CustomSinkConfiguration>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse existing config, starting fresh");
                }
            }

            // Add or update
            configs[config.Name] = config;

            yamlToWrite = _serializer.Serialize(configs);
        }
        finally
        {
            _configLock.ExitWriteLock();
        }

        // Write file outside the lock
        try
        {
            File.WriteAllText(_configPath, yamlToWrite);
            _logger.LogDebug("Saved custom sink configuration for '{Name}'", config.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save custom sink configuration");
        }
    }

    private void RemoveConfiguration(string name)
    {
        // Read existing config file outside the lock
        string? existingYaml = null;
        bool fileExists;
        try
        {
            fileExists = File.Exists(_configPath);
            if (fileExists)
            {
                existingYaml = File.ReadAllText(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read config for removal of sink '{Name}'", name);
            return;
        }

        if (!fileExists || string.IsNullOrWhiteSpace(existingYaml))
            return;

        // Process under write lock
        string? yamlToWrite = null;
        bool removed;
        _configLock.EnterWriteLock();
        try
        {
            var configs = _deserializer.Deserialize<Dictionary<string, CustomSinkConfiguration>>(existingYaml)
                ?? new Dictionary<string, CustomSinkConfiguration>();

            removed = configs.Remove(name);
            if (removed)
            {
                yamlToWrite = _serializer.Serialize(configs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process removal of sink '{Name}' from configuration", name);
            return;
        }
        finally
        {
            _configLock.ExitWriteLock();
        }

        // Write file outside the lock if we removed the sink
        if (removed && yamlToWrite != null)
        {
            try
            {
                File.WriteAllText(_configPath, yamlToWrite);
                _logger.LogDebug("Removed sink '{Name}' from configuration", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save config after removing sink '{Name}'", name);
            }
        }
    }

    private static void ValidateSinkName(string name)
    {
        if (!PaModuleRunner.ValidateName(name, out var error))
        {
            throw new ArgumentException(error, nameof(name));
        }
    }

    private static CustomSinkResponse ToResponse(string name, CustomSinkContext context)
    {
        var config = context.Config;
        return new CustomSinkResponse(
            Name: name,
            Type: config.Type,
            State: context.State,
            Description: config.Description,
            ModuleIndex: context.ModuleIndex,
            PulseAudioSinkName: name, // Same as sink_name parameter
            ErrorMessage: context.ErrorMessage,
            CreatedAt: context.CreatedAt,
            Slaves: config.Slaves,
            MasterSink: config.MasterSink,
            Channels: config.Type == CustomSinkType.Remap ? config.Channels : null,
            ChannelMappings: config.ChannelMappings
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}
