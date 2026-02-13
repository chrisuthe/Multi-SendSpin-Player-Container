using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MultiRoomAudio.Audio;
using MultiRoomAudio.Exceptions;
using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages custom PulseAudio sinks (combine-sink and remap-sink).
/// Implements IHostedService for startup sink creation and shutdown cleanup.
/// </summary>
public class CustomSinksService : IAsyncDisposable
{
    private readonly ILogger<CustomSinksService> _logger;
    private readonly IPaModuleRunner _moduleRunner;
    private readonly EnvironmentService _environment;
    private readonly IServiceProvider _services;
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
        EnvironmentService environment,
        IServiceProvider services)
    {
        _logger = logger;
        _moduleRunner = moduleRunner;
        _environment = environment;
        _services = services;
        _configPath = Path.Combine(environment.ConfigPath, "custom-sinks.yaml");

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithQuotingNecessaryStrings()
            .Build();
    }

    /// <summary>
    /// Load and start persisted sinks on startup.
    /// Called by StartupOrchestrator during background initialization.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CustomSinksService starting...");

        // Load configurations from YAML
        var configs = LoadConfigurations();

        if (configs.Count == 0)
        {
            _logger.LogInformation("No custom sinks configured");
            return;
        }

        // Migrate old configs that don't have identifiers or have stale sink names
        configs = MigrateConfigurations(configs);

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
    /// Called by StartupOrchestrator during graceful shutdown.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
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
            throw new EntityAlreadyExistsException("PulseAudio sink", request.Name);
        }

        // Get slave identifiers for future migration
        var backend = _services.GetService<BackendFactory>();
        var slaveIdentifiers = request.Slaves
            .Select(s => ExtractIdentifiers(backend?.GetDevice(s)))
            .ToList();

        var config = new CustomSinkConfiguration
        {
            Name = request.Name,
            Type = CustomSinkType.Combine,
            Description = request.Description,
            Slaves = request.Slaves,
            SlaveIdentifiers = slaveIdentifiers!
        };

        var context = new CustomSinkContext(config, DateTime.UtcNow)
        {
            State = CustomSinkState.Loading
        };

        // TryAdd is atomic - if another thread added the same name, we fail gracefully
        if (!_sinks.TryAdd(request.Name, context))
        {
            throw new EntityAlreadyExistsException("Sink", request.Name);
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
            throw new EntityAlreadyExistsException("PulseAudio sink", request.Name);
        }

        // Build channel maps
        var channelMap = string.Join(",", request.ChannelMappings.Select(m => m.OutputChannel));
        var masterChannelMap = string.Join(",", request.ChannelMappings.Select(m => m.MasterChannel));

        // Get master sink identifiers for future migration
        var backend = _services.GetService<BackendFactory>();
        var masterDevice = backend?.GetDevice(request.MasterSink);
        var masterIdentifiers = ExtractIdentifiers(masterDevice);

        var config = new CustomSinkConfiguration
        {
            Name = request.Name,
            Type = CustomSinkType.Remap,
            Description = request.Description,
            MasterSink = request.MasterSink,
            MasterSinkIdentifiers = masterIdentifiers,
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
            throw new EntityAlreadyExistsException("Sink", request.Name);
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
    public Task<CustomSinkResponse> ImportSinkAsync(
        DetectedSink detected,
        CancellationToken cancellationToken = default)
    {
        ValidateSinkName(detected.SinkName);

        if (_sinks.ContainsKey(detected.SinkName))
        {
            throw new EntityAlreadyExistsException("Sink", detected.SinkName);
        }

        // Get backend for resolving identifiers
        var backend = _services.GetService<BackendFactory>();

        CustomSinkConfiguration config;

        if (detected.Type == CustomSinkType.Combine)
        {
            // Get slave identifiers for future migration
            var slaveIdentifiers = (detected.Slaves ?? [])
                .Select(s => ExtractIdentifiers(backend?.GetDevice(s)))
                .ToList();

            config = new CustomSinkConfiguration
            {
                Name = detected.SinkName,
                Type = CustomSinkType.Combine,
                Description = detected.Description,
                Slaves = detected.Slaves ?? [],
                SlaveIdentifiers = slaveIdentifiers!
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

            // Get master sink identifiers for future migration
            var masterDevice = detected.MasterSink != null ? backend?.GetDevice(detected.MasterSink) : null;
            var masterIdentifiers = ExtractIdentifiers(masterDevice);

            config = new CustomSinkConfiguration
            {
                Name = detected.SinkName,
                Type = CustomSinkType.Remap,
                Description = detected.Description,
                MasterSink = detected.MasterSink,
                MasterSinkIdentifiers = masterIdentifiers,
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

        return Task.FromResult(ToResponse(detected.SinkName, context));
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

    /// <summary>
    /// Migrates sink configurations by resolving stale master/slave sink names using stored identifiers.
    /// This handles ALSA card number changes after reboot.
    /// </summary>
    private List<CustomSinkConfiguration> MigrateConfigurations(List<CustomSinkConfiguration> configs)
    {
        // Get current devices to resolve identifiers
        var backend = _services.GetService<BackendFactory>();
        if (backend == null)
        {
            _logger.LogWarning("BackendFactory not available, skipping sink configuration migration");
            return configs;
        }

        var currentDevices = backend.GetOutputDevices().ToList();
        if (currentDevices.Count == 0)
        {
            _logger.LogDebug("No devices available for migration resolution");
            return configs;
        }

        // Get cards for profile lookup (resolve lazily to avoid DI circular dependency)
        var cardProfileService = _services.GetService<CardProfileService>();
        var cards = cardProfileService?.GetCards().ToList() ?? [];

        var needsSave = false;

        foreach (var config in configs)
        {
            if (config.Type == CustomSinkType.Remap && !string.IsNullOrEmpty(config.MasterSink))
            {
                // Check if master sink exists
                var masterExists = currentDevices.Any(d => d.Id.Equals(config.MasterSink, StringComparison.OrdinalIgnoreCase));

                // Proactively extract identifiers if missing (even if sink name still exists)
                if (masterExists && (config.MasterSinkIdentifiers == null || !config.MasterSinkIdentifiers.HasStableIdentifier()))
                {
                    var currentDevice = currentDevices.FirstOrDefault(d =>
                        d.Id.Equals(config.MasterSink, StringComparison.OrdinalIgnoreCase));
                    if (currentDevice != null)
                    {
                        config.MasterSinkIdentifiers = ExtractIdentifiers(currentDevice, cards);
                        needsSave = true;
                        _logger.LogInformation(
                            "Enriched remap sink '{Name}' with identifiers (BusPath: {BusPath}, CardProfile: {CardProfile})",
                            config.Name,
                            config.MasterSinkIdentifiers?.BusPath ?? "none",
                            config.MasterSinkIdentifiers?.CardProfile ?? "none");
                    }
                }

                // Verify identity: sink name exists but might point to WRONG device after ALSA renumbering
                // (e.g., USB inserted causes PCIe cards to shift indices, alsa_card_0 now refers to different hardware)
                if (masterExists && config.MasterSinkIdentifiers?.HasStableIdentifier() == true)
                {
                    var currentDevice = currentDevices.FirstOrDefault(d =>
                        d.Id.Equals(config.MasterSink, StringComparison.OrdinalIgnoreCase));

                    if (currentDevice?.Identifiers != null && !IdentifiersMatch(currentDevice.Identifiers, config.MasterSinkIdentifiers))
                    {
                        // Name exists but points to DIFFERENT device! Resolve to find correct one
                        _logger.LogWarning(
                            "Sink '{Name}' master '{MasterSink}' exists but points to different device (expected BusPath: {Expected}, actual: {Actual})",
                            config.Name, config.MasterSink,
                            config.MasterSinkIdentifiers.BusPath ?? "none",
                            currentDevice.Identifiers.BusPath ?? "none");

                        var resolvedSink = ResolveSinkByIdentifiers(config.MasterSinkIdentifiers, currentDevices);
                        if (resolvedSink != null)
                        {
                            _logger.LogInformation(
                                "Resolved sink '{Name}' master to correct device '{New}' (was incorrectly pointing to '{Old}')",
                                config.Name, resolvedSink.Id, config.MasterSink);
                            config.MasterSink = resolvedSink.Id;
                            config.MasterSinkIdentifiers.LastKnownSinkName = resolvedSink.Id;
                            needsSave = true;
                        }
                        else
                        {
                            _logger.LogError(
                                "Could not find correct device for sink '{Name}' - device with BusPath '{BusPath}' not found",
                                config.Name, config.MasterSinkIdentifiers.BusPath);
                        }
                        // Skip the other branches since we've handled this case
                        continue;
                    }
                }

                if (!masterExists && config.MasterSinkIdentifiers != null)
                {
                    // Try to resolve using identifiers
                    var resolvedSink = ResolveSinkByIdentifiers(config.MasterSinkIdentifiers, currentDevices);
                    if (resolvedSink != null)
                    {
                        _logger.LogInformation(
                            "Migrated sink '{Name}' master from '{Old}' to '{New}' (resolved by {Method})",
                            config.Name, config.MasterSink, resolvedSink.Id, GetResolutionMethod(config.MasterSinkIdentifiers, resolvedSink));
                        config.MasterSink = resolvedSink.Id;
                        config.MasterSinkIdentifiers.LastKnownSinkName = resolvedSink.Id;
                        needsSave = true;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Could not resolve master sink for '{Name}' - original '{MasterSink}' not found and identifiers didn't match any device",
                            config.Name, config.MasterSink);
                    }
                }
                else if (!masterExists)
                {
                    // Fallback: try to resolve using sink name pattern (for old configs without identifiers)
                    var resolvedSink = ResolveSinkByNamePattern(config.MasterSink, currentDevices);
                    if (resolvedSink != null)
                    {
                        _logger.LogInformation(
                            "Migrated sink '{Name}' master from '{Old}' to '{New}' (resolved by name pattern)",
                            config.Name, config.MasterSink, resolvedSink.Id);
                        config.MasterSink = resolvedSink.Id;
                        // Store identifiers for future migrations
                        config.MasterSinkIdentifiers = ExtractIdentifiers(resolvedSink, cards);
                        needsSave = true;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Master sink '{MasterSink}' for '{Name}' not found and no identifiers stored for migration",
                            config.MasterSink, config.Name);
                    }
                }
            }
            else if (config.Type == CustomSinkType.Combine && config.Slaves?.Count > 0)
            {
                // Check and resolve each slave sink
                var resolvedSlaves = new List<string>();
                var slaveIdentifiers = config.SlaveIdentifiers ?? [];

                for (int i = 0; i < config.Slaves.Count; i++)
                {
                    var slave = config.Slaves[i];
                    var slaveExists = currentDevices.Any(d => d.Id.Equals(slave, StringComparison.OrdinalIgnoreCase));

                    if (!slaveExists && i < slaveIdentifiers.Count && slaveIdentifiers[i] != null)
                    {
                        var resolvedSink = ResolveSinkByIdentifiers(slaveIdentifiers[i], currentDevices);
                        if (resolvedSink != null)
                        {
                            _logger.LogInformation(
                                "Migrated sink '{Name}' slave[{Index}] from '{Old}' to '{New}'",
                                config.Name, i, slave, resolvedSink.Id);
                            resolvedSlaves.Add(resolvedSink.Id);
                            slaveIdentifiers[i].LastKnownSinkName = resolvedSink.Id;
                            needsSave = true;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Could not resolve slave[{Index}] for '{Name}' - original '{Slave}' not found",
                                i, config.Name, slave);
                            resolvedSlaves.Add(slave); // Keep original (will fail to load)
                        }
                    }
                    else if (!slaveExists)
                    {
                        // Fallback: try to resolve using sink name pattern (for old configs without identifiers)
                        var resolvedSink = ResolveSinkByNamePattern(slave, currentDevices);
                        if (resolvedSink != null)
                        {
                            _logger.LogInformation(
                                "Migrated sink '{Name}' slave[{Index}] from '{Old}' to '{New}' (resolved by name pattern)",
                                config.Name, i, slave, resolvedSink.Id);
                            resolvedSlaves.Add(resolvedSink.Id);
                            // Store identifiers for future migrations
                            if (config.SlaveIdentifiers == null)
                                config.SlaveIdentifiers = new List<SinkIdentifiersConfig>();
                            while (config.SlaveIdentifiers.Count <= i)
                                config.SlaveIdentifiers.Add(null!);
                            config.SlaveIdentifiers[i] = ExtractIdentifiers(resolvedSink, cards)!;
                            needsSave = true;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Slave sink '{Slave}' for '{Name}' not found and no identifiers stored",
                                slave, config.Name);
                            resolvedSlaves.Add(slave);
                        }
                    }
                    else
                    {
                        // Proactively enrich existing slave with identifiers if missing
                        if (i >= slaveIdentifiers.Count || slaveIdentifiers[i] == null || !slaveIdentifiers[i].HasStableIdentifier())
                        {
                            var existingDevice = currentDevices.FirstOrDefault(d =>
                                d.Id.Equals(slave, StringComparison.OrdinalIgnoreCase));
                            if (existingDevice != null)
                            {
                                if (config.SlaveIdentifiers == null)
                                    config.SlaveIdentifiers = new List<SinkIdentifiersConfig>();
                                while (config.SlaveIdentifiers.Count <= i)
                                    config.SlaveIdentifiers.Add(null!);
                                config.SlaveIdentifiers[i] = ExtractIdentifiers(existingDevice, cards)!;
                                needsSave = true;
                                _logger.LogInformation(
                                    "Enriched combine sink '{Name}' slave[{Index}] with identifiers (BusPath: {BusPath})",
                                    config.Name, i, config.SlaveIdentifiers[i]?.BusPath ?? "none");
                            }
                        }

                        // Verify identity: slave name exists but might point to WRONG device after ALSA renumbering
                        var storedIdentifiers = i < slaveIdentifiers.Count ? slaveIdentifiers[i] : null;
                        if (storedIdentifiers?.HasStableIdentifier() == true)
                        {
                            var currentDevice = currentDevices.FirstOrDefault(d =>
                                d.Id.Equals(slave, StringComparison.OrdinalIgnoreCase));

                            if (currentDevice?.Identifiers != null && !IdentifiersMatch(currentDevice.Identifiers, storedIdentifiers))
                            {
                                // Name exists but points to DIFFERENT device! Resolve to find correct one
                                _logger.LogWarning(
                                    "Combine sink '{Name}' slave[{Index}] '{Slave}' exists but points to different device",
                                    config.Name, i, slave);

                                var resolvedSink = ResolveSinkByIdentifiers(storedIdentifiers, currentDevices);
                                if (resolvedSink != null)
                                {
                                    _logger.LogInformation(
                                        "Resolved combine sink '{Name}' slave[{Index}] to correct device '{New}'",
                                        config.Name, i, resolvedSink.Id);
                                    resolvedSlaves.Add(resolvedSink.Id);
                                    storedIdentifiers.LastKnownSinkName = resolvedSink.Id;
                                    needsSave = true;
                                    continue;
                                }
                            }
                        }

                        resolvedSlaves.Add(slave);
                    }
                }

                config.Slaves = resolvedSlaves;
            }
        }

        // Save migrated configs
        if (needsSave)
        {
            SaveAllConfigurations(configs);
        }

        return configs;
    }

    /// <summary>
    /// Resolves a sink using stored identifiers, matching by priority: BusPath > AlsaLongCardName > Serial > VID/PID.
    /// When multiple sinks match, prefers sinks with a profile suffix matching the stored CardProfile.
    /// </summary>
    private AudioDevice? ResolveSinkByIdentifiers(SinkIdentifiersConfig identifiers, List<AudioDevice> devices)
    {
        // Extract profile suffix from CardProfile
        // e.g., "output:analog-surround-71" → "analog-surround-71"
        // e.g., "output:analog-stereo+input:analog-stereo" → "analog-stereo" (output portion only)
        string? profileSuffix = null;
        if (!string.IsNullOrEmpty(identifiers.CardProfile))
        {
            profileSuffix = identifiers.CardProfile;

            // Remove "output:" prefix if present
            var colonIdx = profileSuffix.IndexOf(':');
            if (colonIdx >= 0)
                profileSuffix = profileSuffix.Substring(colonIdx + 1);

            // Handle combined profiles: strip "+input:..." portion
            // PulseAudio names sinks based only on the output profile part
            var plusIdx = profileSuffix.IndexOf("+input:", StringComparison.OrdinalIgnoreCase);
            if (plusIdx > 0)
                profileSuffix = profileSuffix.Substring(0, plusIdx);
        }

        // Priority 1: Bus path (most stable for USB devices)
        if (!string.IsNullOrEmpty(identifiers.BusPath))
        {
            var matches = devices.Where(d =>
                d.Identifiers?.BusPath != null &&
                d.Identifiers.BusPath.Equals(identifiers.BusPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count > 0)
            {
                // Prefer sink with matching profile
                if (!string.IsNullOrEmpty(profileSuffix))
                {
                    var profileMatch = matches.FirstOrDefault(d =>
                        d.Id.EndsWith("." + profileSuffix, StringComparison.OrdinalIgnoreCase));
                    if (profileMatch != null)
                        return profileMatch;

                    _logger.LogWarning(
                        "No sink found matching profile '{Profile}' for bus path '{BusPath}', using first available",
                        identifiers.CardProfile, identifiers.BusPath);
                }
                return matches.First();
            }
        }

        // Priority 2: ALSA long card name (stable for PCIe devices)
        if (!string.IsNullOrEmpty(identifiers.AlsaLongCardName))
        {
            var matches = devices.Where(d =>
                d.Identifiers?.AlsaLongCardName != null &&
                d.Identifiers.AlsaLongCardName.Equals(identifiers.AlsaLongCardName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count > 0)
            {
                // Prefer sink with matching profile
                if (!string.IsNullOrEmpty(profileSuffix))
                {
                    var profileMatch = matches.FirstOrDefault(d =>
                        d.Id.EndsWith("." + profileSuffix, StringComparison.OrdinalIgnoreCase));
                    if (profileMatch != null)
                        return profileMatch;

                    _logger.LogWarning(
                        "No sink found matching profile '{Profile}' for ALSA card '{CardName}', using first available",
                        identifiers.CardProfile, identifiers.AlsaLongCardName);
                }
                return matches.First();
            }
        }

        // Priority 3: Serial number (may not be unique)
        if (!string.IsNullOrEmpty(identifiers.Serial))
        {
            var matches = devices.Where(d =>
                d.Identifiers?.Serial != null &&
                d.Identifiers.Serial.Equals(identifiers.Serial, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count > 0)
            {
                // Prefer sink with matching profile
                if (!string.IsNullOrEmpty(profileSuffix))
                {
                    var profileMatch = matches.FirstOrDefault(d =>
                        d.Id.EndsWith("." + profileSuffix, StringComparison.OrdinalIgnoreCase));
                    if (profileMatch != null)
                        return profileMatch;
                }
                return matches.First();
            }
        }

        // Priority 4: VID/PID combination (will match first device of same type)
        if (!string.IsNullOrEmpty(identifiers.VendorId) && !string.IsNullOrEmpty(identifiers.ProductId))
        {
            var matches = devices.Where(d =>
                d.Identifiers?.VendorId != null &&
                d.Identifiers?.ProductId != null &&
                d.Identifiers.VendorId.Equals(identifiers.VendorId, StringComparison.OrdinalIgnoreCase) &&
                d.Identifiers.ProductId.Equals(identifiers.ProductId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count > 0)
            {
                // Prefer sink with matching profile
                if (!string.IsNullOrEmpty(profileSuffix))
                {
                    var profileMatch = matches.FirstOrDefault(d =>
                        d.Id.EndsWith("." + profileSuffix, StringComparison.OrdinalIgnoreCase));
                    if (profileMatch != null)
                        return profileMatch;
                }
                return matches.First();
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a device's identifiers match the stored identifiers.
    /// Returns true if ANY stable identifier matches (BusPath preferred, then AlsaLongCardName, Serial, or VID/PID).
    /// </summary>
    private static bool IdentifiersMatch(DeviceIdentifiers device, SinkIdentifiersConfig stored)
    {
        // Priority 1: BusPath (most reliable for USB devices, tied to physical port)
        if (!string.IsNullOrEmpty(stored.BusPath) && !string.IsNullOrEmpty(device.BusPath))
        {
            return stored.BusPath.Equals(device.BusPath, StringComparison.OrdinalIgnoreCase);
        }

        // Priority 2: AlsaLongCardName (stable for PCIe devices)
        if (!string.IsNullOrEmpty(stored.AlsaLongCardName) && !string.IsNullOrEmpty(device.AlsaLongCardName))
        {
            return stored.AlsaLongCardName.Equals(device.AlsaLongCardName, StringComparison.OrdinalIgnoreCase);
        }

        // Priority 3: Serial (may not be unique across identical devices)
        if (!string.IsNullOrEmpty(stored.Serial) && !string.IsNullOrEmpty(device.Serial))
        {
            return stored.Serial.Equals(device.Serial, StringComparison.OrdinalIgnoreCase);
        }

        // Priority 4: VID/PID combination
        if (!string.IsNullOrEmpty(stored.VendorId) && !string.IsNullOrEmpty(stored.ProductId) &&
            !string.IsNullOrEmpty(device.VendorId) && !string.IsNullOrEmpty(device.ProductId))
        {
            return stored.VendorId.Equals(device.VendorId, StringComparison.OrdinalIgnoreCase) &&
                   stored.ProductId.Equals(device.ProductId, StringComparison.OrdinalIgnoreCase);
        }

        // No stable identifiers to compare - can't verify
        return true;
    }

    /// <summary>
    /// Fallback resolution for old configs without stored identifiers.
    /// Extracts device identifier from old sink name pattern and matches against current devices.
    /// </summary>
    /// <remarks>
    /// Sink name patterns:
    /// - alsa_output.pci-0000_01_00.0.analog-surround-71 → extract "pci-0000_01_00.0"
    /// - alsa_output.usb-0d8c_USB_Sound_Device-00.analog-stereo → extract "usb-0d8c_USB_Sound_Device-00"
    /// - alsa_output.platform-soc_audio.stereo-fallback → extract "platform-soc_audio"
    ///
    /// For "alsa_card_N" style names, we extract the profile and match by profile suffix.
    /// </remarks>
    private AudioDevice? ResolveSinkByNamePattern(string oldSinkName, List<AudioDevice> devices)
    {
        if (string.IsNullOrEmpty(oldSinkName))
            return null;

        // Extract the device identifier and profile from sink name
        // Format: alsa_output.<device-identifier>.<profile>
        // Note: Device identifiers can contain dots (e.g., pci-0000_01_00.0)
        // Profiles typically start with: analog-, digital-, iec958-, hdmi-, pro-audio, stereo-fallback
        var (deviceIdentifier, profile) = ParseSinkName(oldSinkName);
        if (deviceIdentifier == null)
            return null;

        // Case 1: PCI device (e.g., "pci-0000_01_00.0")
        // The PCI address is stable across reboots
        if (deviceIdentifier.StartsWith("pci-", StringComparison.OrdinalIgnoreCase))
        {
            // Extract full PCI address (e.g., "0000_01_00.0" or "0000:01:00.0")
            var pciAddress = deviceIdentifier.Substring(4);

            var match = devices.FirstOrDefault(d =>
            {
                if (d.Id == null) return false;
                var (currentDeviceId, _) = ParseSinkName(d.Id);
                if (currentDeviceId == null || !currentDeviceId.StartsWith("pci-", StringComparison.OrdinalIgnoreCase))
                    return false;
                var currentPciAddress = currentDeviceId.Substring(4);
                // Normalize for comparison (underscores vs colons, etc.)
                return NormalizePciAddress(currentPciAddress).Equals(
                    NormalizePciAddress(pciAddress), StringComparison.OrdinalIgnoreCase);
            });

            if (match != null)
            {
                _logger.LogDebug("Matched old sink '{Old}' to '{New}' by PCI address '{Pci}'",
                    oldSinkName, match.Id, pciAddress);
                return match;
            }
        }

        // Case 2: USB device (e.g., "usb-0d8c_USB_Sound_Device-00")
        // Contains VID and product name which are stable
        if (deviceIdentifier.StartsWith("usb-", StringComparison.OrdinalIgnoreCase))
        {
            // Extract VID from USB identifier (first 4 hex chars after "usb-")
            var usbInfo = deviceIdentifier.Substring(4);
            var vidMatch = System.Text.RegularExpressions.Regex.Match(usbInfo, @"^([0-9a-fA-F]{4})");
            if (vidMatch.Success)
            {
                var vid = vidMatch.Groups[1].Value.ToLowerInvariant();

                // Try to match by VID and similar product name
                var match = devices.FirstOrDefault(d =>
                {
                    if (d.Id == null) return false;
                    var (currentDeviceId, _) = ParseSinkName(d.Id);
                    if (currentDeviceId == null || !currentDeviceId.StartsWith("usb-", StringComparison.OrdinalIgnoreCase))
                        return false;
                    return currentDeviceId.Contains(vid, StringComparison.OrdinalIgnoreCase);
                });

                if (match != null)
                {
                    _logger.LogDebug("Matched old sink '{Old}' to '{New}' by USB VID '{Vid}'",
                        oldSinkName, match.Id, vid);
                    return match;
                }
            }
        }

        // Case 3: alsa_card_N style (e.g., "alsa_card_0")
        // Card numbers are NOT stable - use card identifiers from card-profiles.yaml
        if (deviceIdentifier.StartsWith("alsa_card_", StringComparison.OrdinalIgnoreCase))
        {
            // Try to find matching card by profile, then use its stable identifier
            // Only attempt if we have a non-empty profile to match against
            var cardService = _services.GetService<CardProfileService>();
            if (cardService != null && !string.IsNullOrEmpty(profile))
            {
                var cards = cardService.GetCards().ToList();

                // Find card whose active profile matches our sink's profile
                // e.g., if sink profile is "analog-surround-71", card active profile might be "output:analog-surround-71+input:..."
                var matchingCard = cards.FirstOrDefault(c =>
                    c.ActiveProfile?.Contains(profile, StringComparison.OrdinalIgnoreCase) == true);

                if (matchingCard != null)
                {
                    // Use card's identifier to find matching device
                    var cardIdentifier = matchingCard.Name
                        .Replace("alsa_card.", "")
                        .Replace("bluez_card.", "");
                    var match = devices.FirstOrDefault(d =>
                        d.Id != null && d.Id.Contains(cardIdentifier, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        _logger.LogDebug("Matched old sink '{Old}' to '{New}' via card '{Card}' identifier",
                            oldSinkName, match.Id, matchingCard.Name);
                        return match;
                    }
                }
            }

            // Fallback: use devices.yaml historical sink names
            // This works even when the card profile has changed, because we use the historical record
            // of which device previously had this profile
            var configService = _services.GetService<ConfigurationService>();
            if (configService != null && !string.IsNullOrEmpty(profile))
            {
                var deviceConfigs = configService.GetAllDeviceConfigurations();

                // Extract card number from old sink name (e.g., "alsa_card_0" -> "0")
                // This helps distinguish between multiple cards that might have the same profile
                string? oldCardNumber = null;
                var cardMatch = System.Text.RegularExpressions.Regex.Match(
                    deviceIdentifier, @"alsa_card_(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (cardMatch.Success)
                {
                    oldCardNumber = cardMatch.Groups[1].Value;
                }

                // Find device configs that match our target profile
                var matchingConfigs = deviceConfigs.Values.Where(dc =>
                    dc.LastKnownSinkName != null &&
                    dc.LastKnownSinkName.EndsWith("." + profile, StringComparison.OrdinalIgnoreCase)).ToList();

                // If we have a card number, prefer configs that had the same card number
                DeviceConfiguration? matchingConfig = null;
                if (oldCardNumber != null && matchingConfigs.Count > 1)
                {
                    // Prefer the config whose LastKnownSinkName had the same card number
                    matchingConfig = matchingConfigs.FirstOrDefault(dc =>
                        dc.LastKnownSinkName!.Contains($"alsa_card_{oldCardNumber}.", StringComparison.OrdinalIgnoreCase) ||
                        dc.LastKnownSinkName!.Contains($"alsa_card_{oldCardNumber}+", StringComparison.OrdinalIgnoreCase));
                }

                // Fall back to first match if no card number match found
                matchingConfig ??= matchingConfigs.FirstOrDefault();

                if (matchingConfig?.Identifiers != null)
                {
                    // Use the stored identifiers to find the matching current device
                    var identifiers = new SinkIdentifiersConfig
                    {
                        BusPath = matchingConfig.Identifiers.BusPath,
                        AlsaLongCardName = matchingConfig.Identifiers.AlsaLongCardName,
                        Serial = matchingConfig.Identifiers.Serial,
                        VendorId = matchingConfig.Identifiers.VendorId,
                        ProductId = matchingConfig.Identifiers.ProductId
                    };

                    var match = ResolveSinkByIdentifiers(identifiers, devices);
                    if (match != null)
                    {
                        _logger.LogDebug(
                            "Matched old sink '{Old}' to '{New}' via devices.yaml historical sink name '{Historical}' (card number match: {CardMatch})",
                            oldSinkName, match.Id, matchingConfig.LastKnownSinkName, oldCardNumber != null);
                        return match;
                    }
                }
            }

            // Fallback: match by profile suffix (weak match)
            var profileMatch = devices.FirstOrDefault(d =>
                d.Id != null && profile != null && d.Id.EndsWith("." + profile, StringComparison.OrdinalIgnoreCase));

            if (profileMatch != null)
            {
                _logger.LogDebug("Matched old sink '{Old}' to '{New}' by profile suffix '{Profile}' (weak match)",
                    oldSinkName, profileMatch.Id, profile);
                return profileMatch;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a PulseAudio sink name into device identifier and profile.
    /// Handles device identifiers that contain dots (e.g., PCI addresses).
    /// </summary>
    /// <remarks>
    /// Profiles typically start with: analog-, digital-, iec958-, hdmi-, pro-audio, stereo-fallback
    /// Examples:
    /// - alsa_output.pci-0000_01_00.0.analog-surround-71 → ("pci-0000_01_00.0", "analog-surround-71")
    /// - alsa_output.usb-0d8c_USB_Sound_Device-00.analog-stereo → ("usb-0d8c_USB_Sound_Device-00", "analog-stereo")
    /// </remarks>
    private static (string? deviceIdentifier, string? profile) ParseSinkName(string sinkName)
    {
        if (string.IsNullOrEmpty(sinkName))
            return (null, null);

        // Must start with alsa_output.
        const string prefix = "alsa_output.";
        if (!sinkName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var remainder = sinkName.Substring(prefix.Length);

        // Known profile prefixes - find where the profile starts
        string[] profilePrefixes = ["analog-", "digital-", "iec958-", "hdmi-", "pro-audio", "stereo-fallback", "multichannel-"];

        // Search for profile start from the end (profiles are typically at the end)
        var lastDotIndex = -1;
        foreach (var profilePrefix in profilePrefixes)
        {
            var idx = remainder.LastIndexOf("." + profilePrefix, StringComparison.OrdinalIgnoreCase);
            if (idx > lastDotIndex)
                lastDotIndex = idx;
        }

        if (lastDotIndex > 0)
        {
            var deviceIdentifier = remainder.Substring(0, lastDotIndex);
            var profile = remainder.Substring(lastDotIndex + 1);
            return (deviceIdentifier, profile);
        }

        // Fallback: split on first dot (may be incorrect for dotted device IDs)
        var firstDot = remainder.IndexOf('.');
        if (firstDot > 0)
        {
            return (remainder.Substring(0, firstDot), remainder.Substring(firstDot + 1));
        }

        return (remainder, null);
    }

    /// <summary>
    /// Normalizes a PCI address for comparison (handles _ vs : vs . separators).
    /// </summary>
    private static string NormalizePciAddress(string pciAddress)
    {
        // Replace all separators with dots for consistent comparison
        return pciAddress.Replace("_", ".").Replace(":", ".").ToLowerInvariant();
    }

    /// <summary>
    /// Gets the method used to resolve a sink for logging purposes.
    /// </summary>
    private static string GetResolutionMethod(SinkIdentifiersConfig identifiers, AudioDevice device)
    {
        if (!string.IsNullOrEmpty(identifiers.BusPath) &&
            device.Identifiers?.BusPath?.Equals(identifiers.BusPath, StringComparison.OrdinalIgnoreCase) == true)
            return "bus path";

        if (!string.IsNullOrEmpty(identifiers.AlsaLongCardName) &&
            device.Identifiers?.AlsaLongCardName?.Equals(identifiers.AlsaLongCardName, StringComparison.OrdinalIgnoreCase) == true)
            return "ALSA long card name";

        if (!string.IsNullOrEmpty(identifiers.Serial) &&
            device.Identifiers?.Serial?.Equals(identifiers.Serial, StringComparison.OrdinalIgnoreCase) == true)
            return "serial number";

        return "VID/PID";
    }

    /// <summary>
    /// Saves all configurations to the config file (used for migration).
    /// </summary>
    private void SaveAllConfigurations(List<CustomSinkConfiguration> configs)
    {
        _configLock.EnterWriteLock();
        try
        {
            var dict = configs.ToDictionary(c => c.Name);
            var yaml = _serializer.Serialize(dict);
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_configPath, yaml);
            _logger.LogInformation("Saved migrated custom sink configurations");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save migrated custom sink configurations");
        }
        finally
        {
            _configLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Extracts identifiers from an audio device for storing in sink configuration.
    /// Optionally looks up the card profile by bus path if cards are provided.
    /// </summary>
    private static SinkIdentifiersConfig? ExtractIdentifiers(AudioDevice? device, IEnumerable<PulseAudioCard>? cards = null)
    {
        if (device?.Identifiers == null)
            return null;

        // Find matching card by bus path to get the configured profile
        string? cardProfile = null;
        if (!string.IsNullOrEmpty(device.Identifiers.BusPath) && cards != null)
        {
            var matchingCard = cards.FirstOrDefault(c =>
                c.Identifiers?.BusPath != null &&
                c.Identifiers.BusPath.Equals(device.Identifiers.BusPath, StringComparison.OrdinalIgnoreCase));

            if (matchingCard != null)
            {
                cardProfile = matchingCard.ActiveProfile;  // e.g., "output:analog-surround-71"
            }
        }

        return new SinkIdentifiersConfig
        {
            BusPath = device.Identifiers.BusPath,
            Serial = device.Identifiers.Serial,
            VendorId = device.Identifiers.VendorId,
            ProductId = device.Identifiers.ProductId,
            AlsaLongCardName = device.Identifiers.AlsaLongCardName,
            LastKnownSinkName = device.Id,
            CardProfile = cardProfile
        };
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
        await ShutdownAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}
