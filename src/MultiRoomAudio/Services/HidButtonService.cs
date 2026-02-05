using System.Collections.Concurrent;
using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages HID button support for USB audio devices.
/// Handles enabling/disabling HID buttons per device, loading PulseAudio modules,
/// and routing hardware volume/mute changes to players.
/// </summary>
public class HidButtonService : IAsyncDisposable
{
    private readonly ILogger<HidButtonService> _logger;
    private readonly PaSinkEventService _sinkEventService;
    private readonly HidInputDeviceDetector _hidDetector;
    private readonly IPaModuleRunner _moduleRunner;
    private readonly ConfigurationService _configService;
    private readonly IServiceProvider _serviceProvider;

    private readonly ConcurrentDictionary<string, HidButtonDeviceState> _deviceStates = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Debounce interval for rapid volume/mute changes (hardware buttons can repeat quickly).
    /// </summary>
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Grace period after processing a change before accepting new changes (prevents feedback loops).
    /// </summary>
    private static readonly TimeSpan ProcessingGracePeriod = TimeSpan.FromMilliseconds(200);

    public HidButtonService(
        ILogger<HidButtonService> logger,
        PaSinkEventService sinkEventService,
        HidInputDeviceDetector hidDetector,
        IPaModuleRunner moduleRunner,
        ConfigurationService configService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _sinkEventService = sinkEventService;
        _hidDetector = hidDetector;
        _moduleRunner = moduleRunner;
        _configService = configService;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Initialize the HID button service.
    /// Loads saved configurations and enables HID buttons for previously-enabled devices.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            _logger.LogInformation("Initializing HID button service");

            // Subscribe to sink change events
            _sinkEventService.SinkChanged += OnSinkChanged;

            // Load saved HID button configurations
            await LoadSavedConfigurationsAsync(cancellationToken);

            _initialized = true;
            _logger.LogInformation("HID button service initialized with {DeviceCount} enabled device(s)",
                _deviceStates.Count(d => d.Value.Enabled));
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task LoadSavedConfigurationsAsync(CancellationToken cancellationToken)
    {
        var devices = _configService.GetAllDeviceConfigurations();

        foreach (var (deviceKey, config) in devices)
        {
            if (config.HidButtons?.Enabled == true)
            {
                // Try to find the current sink name for this device
                var sinkName = config.LastKnownSinkName;
                if (string.IsNullOrEmpty(sinkName))
                {
                    _logger.LogDebug("Device {DeviceKey} has HID buttons enabled but no sink name, skipping",
                        deviceKey);
                    continue;
                }

                var busPath = config.Identifiers?.BusPath;
                var vendorId = config.Identifiers?.VendorId;
                var productId = config.Identifiers?.ProductId;

                // Try to find and enable HID buttons
                var inputDevice = config.HidButtons.LastKnownInputPath;
                if (string.IsNullOrEmpty(inputDevice))
                {
                    inputDevice = _hidDetector.FindInputDevice(busPath, vendorId, productId);
                }

                if (!string.IsNullOrEmpty(inputDevice))
                {
                    try
                    {
                        var moduleIndex = await _moduleRunner.LoadMmkbdEvdevAsync(inputDevice, cancellationToken);
                        if (moduleIndex.HasValue)
                        {
                            var state = new HidButtonDeviceState
                            {
                                SinkName = sinkName,
                                Enabled = true,
                                ModuleIndex = moduleIndex.Value,
                                InputDevicePath = inputDevice
                            };
                            _deviceStates[sinkName] = state;

                            _logger.LogInformation("Enabled HID buttons for device {SinkName} (input: {InputDevice}, module: {ModuleIndex})",
                                sinkName, inputDevice, moduleIndex.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to enable HID buttons for device {DeviceKey} on startup", deviceKey);
                    }
                }
                else
                {
                    _logger.LogWarning("Device {DeviceKey} has HID buttons enabled but input device not found",
                        deviceKey);
                }
            }
        }
    }

    /// <summary>
    /// Check if a device has HID buttons available.
    /// </summary>
    public HidButtonStatusResponse GetHidStatus(AudioDevice device)
    {
        var sinkName = device.Id;
        var busPath = device.Identifiers?.BusPath;
        var vendorId = device.Identifiers?.VendorId;
        var productId = device.Identifiers?.ProductId;

        // Check if we have state for this device
        _deviceStates.TryGetValue(sinkName, out var state);

        // Try to find HID input device
        var inputDevice = state?.InputDevicePath;
        if (string.IsNullOrEmpty(inputDevice))
        {
            inputDevice = _hidDetector.FindInputDevice(busPath, vendorId, productId);
        }

        return new HidButtonStatusResponse(
            DeviceId: sinkName,
            HidButtonsAvailable: !string.IsNullOrEmpty(inputDevice),
            HidButtonsEnabled: state?.Enabled ?? false,
            InputDevicePath: inputDevice,
            ModuleIndex: state?.ModuleIndex,
            ErrorMessage: null
        );
    }

    /// <summary>
    /// Enable HID button support for a device.
    /// </summary>
    public async Task<HidButtonEnableResponse> EnableHidButtonsAsync(
        AudioDevice device,
        CancellationToken cancellationToken = default)
    {
        var sinkName = device.Id;
        var busPath = device.Identifiers?.BusPath;
        var vendorId = device.Identifiers?.VendorId;
        var productId = device.Identifiers?.ProductId;

        _logger.LogInformation("Enabling HID buttons for device {SinkName}", sinkName);

        // Find HID input device
        var inputDevice = _hidDetector.FindInputDevice(busPath, vendorId, productId);
        if (string.IsNullOrEmpty(inputDevice))
        {
            _logger.LogWarning("No HID input device found for {SinkName}", sinkName);
            return new HidButtonEnableResponse(
                Success: false,
                DeviceId: sinkName,
                HidButtonsEnabled: false,
                InputDevicePath: null,
                ModuleIndex: null,
                Message: "No HID input device found. This device may not have hardware volume/mute buttons."
            );
        }

        // Load the module
        try
        {
            var moduleIndex = await _moduleRunner.LoadMmkbdEvdevAsync(inputDevice, cancellationToken);
            if (!moduleIndex.HasValue)
            {
                return new HidButtonEnableResponse(
                    Success: false,
                    DeviceId: sinkName,
                    HidButtonsEnabled: false,
                    InputDevicePath: inputDevice,
                    ModuleIndex: null,
                    Message: "Failed to load PulseAudio module for HID button support."
                );
            }

            // Create/update state
            var state = new HidButtonDeviceState
            {
                SinkName = sinkName,
                Enabled = true,
                ModuleIndex = moduleIndex.Value,
                InputDevicePath = inputDevice
            };
            _deviceStates[sinkName] = state;

            // Save configuration
            var deviceKey = ConfigurationService.GenerateDeviceKey(device);
            SaveHidButtonConfig(deviceKey, true, inputDevice, device);

            _logger.LogInformation("HID buttons enabled for {SinkName} (input: {InputDevice}, module: {ModuleIndex})",
                sinkName, inputDevice, moduleIndex.Value);

            return new HidButtonEnableResponse(
                Success: true,
                DeviceId: sinkName,
                HidButtonsEnabled: true,
                InputDevicePath: inputDevice,
                ModuleIndex: moduleIndex.Value,
                Message: "HID button support enabled. Hardware volume/mute buttons will now control this device."
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable HID buttons for {SinkName}", sinkName);
            return new HidButtonEnableResponse(
                Success: false,
                DeviceId: sinkName,
                HidButtonsEnabled: false,
                InputDevicePath: inputDevice,
                ModuleIndex: null,
                Message: $"Error enabling HID buttons: {ex.Message}"
            );
        }
    }

    /// <summary>
    /// Disable HID button support for a device.
    /// </summary>
    public async Task<HidButtonEnableResponse> DisableHidButtonsAsync(
        AudioDevice device,
        CancellationToken cancellationToken = default)
    {
        var sinkName = device.Id;

        _logger.LogInformation("Disabling HID buttons for device {SinkName}", sinkName);

        if (!_deviceStates.TryRemove(sinkName, out var state))
        {
            // Not enabled - just update config
            var deviceKey = ConfigurationService.GenerateDeviceKey(device);
            SaveHidButtonConfig(deviceKey, false, null, device);

            return new HidButtonEnableResponse(
                Success: true,
                DeviceId: sinkName,
                HidButtonsEnabled: false,
                InputDevicePath: null,
                ModuleIndex: null,
                Message: "HID button support was not enabled for this device."
            );
        }

        // Unload the module
        if (state.ModuleIndex.HasValue)
        {
            try
            {
                await _moduleRunner.UnloadModuleAsync(state.ModuleIndex.Value, cancellationToken);
                _logger.LogDebug("Unloaded module {ModuleIndex} for {SinkName}", state.ModuleIndex.Value, sinkName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unload module {ModuleIndex} for {SinkName}",
                    state.ModuleIndex.Value, sinkName);
            }
        }

        // Save configuration
        var key = ConfigurationService.GenerateDeviceKey(device);
        SaveHidButtonConfig(key, false, null, device);

        _logger.LogInformation("HID buttons disabled for {SinkName}", sinkName);

        return new HidButtonEnableResponse(
            Success: true,
            DeviceId: sinkName,
            HidButtonsEnabled: false,
            InputDevicePath: null,
            ModuleIndex: null,
            Message: "HID button support disabled."
        );
    }

    private void SaveHidButtonConfig(string deviceKey, bool enabled, string? inputPath, AudioDevice device)
    {
        var config = _configService.GetDevice(deviceKey);
        if (config == null)
        {
            config = new DeviceConfiguration
            {
                FirstSeen = DateTime.UtcNow,
                LastKnownSinkName = device.Id,
                Identifiers = DeviceIdentifiersConfig.FromModel(device.Identifiers)
            };
        }

        config.HidButtons = new HidButtonConfiguration
        {
            Enabled = enabled,
            LastKnownInputPath = inputPath
        };
        config.LastSeen = DateTime.UtcNow;

        _configService.SetDevice(deviceKey, config);
        _configService.SaveDevices();
    }

    private void OnSinkChanged(object? sender, SinkChangeEventArgs e)
    {
        // Fire and forget - we don't want to block the event service
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleSinkChangeAsync(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling sink change for {SinkName}", e.SinkName);
            }
        });
    }

    private async Task HandleSinkChangeAsync(SinkChangeEventArgs e)
    {
        // Check if this sink has HID buttons enabled
        if (!_deviceStates.TryGetValue(e.SinkName, out var state) || !state.Enabled)
        {
            return;
        }

        // Layer 1: Check if we're already processing a change (feedback loop prevention)
        if (state.IsProcessingChange)
        {
            _logger.LogDebug("Ignoring sink change for {SinkName} - already processing", e.SinkName);
            return;
        }

        // Layer 2: Check if state actually changed
        if (state.LastKnownVolume == e.VolumePercent && state.LastKnownMuted == e.IsMuted)
        {
            _logger.LogDebug("Ignoring sink change for {SinkName} - no actual change", e.SinkName);
            return;
        }

        // Layer 3: Debounce rapid changes
        var timeSinceLastChange = DateTime.UtcNow - state.LastChangeTime;
        if (timeSinceLastChange < DebounceInterval)
        {
            // Update pending state - let debounce handle it
            state.PendingVolume = e.VolumePercent;
            state.PendingMuted = e.IsMuted;
            _logger.LogDebug("Debouncing sink change for {SinkName}", e.SinkName);

            // Schedule processing after debounce interval
            _ = Task.Delay(DebounceInterval).ContinueWith(_ => ProcessPendingChanges(e.SinkName));
            return;
        }

        // Process the change
        await ProcessChangeAsync(e.SinkName, e.VolumePercent, e.IsMuted, state);
    }

    private async void ProcessPendingChanges(string sinkName)
    {
        try
        {
            if (!_deviceStates.TryGetValue(sinkName, out var state) || !state.Enabled)
                return;

            if (!state.PendingVolume.HasValue && !state.PendingMuted.HasValue)
                return;

            var volume = state.PendingVolume ?? state.LastKnownVolume;
            var muted = state.PendingMuted ?? state.LastKnownMuted;

            state.PendingVolume = null;
            state.PendingMuted = null;

            await ProcessChangeAsync(sinkName, volume, muted, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pending changes for {SinkName}", sinkName);
        }
    }

    private async Task ProcessChangeAsync(string sinkName, int volume, bool muted, HidButtonDeviceState state)
    {
        try
        {
            state.IsProcessingChange = true;
            state.LastKnownVolume = volume;
            state.LastKnownMuted = muted;
            state.LastChangeTime = DateTime.UtcNow;

            _logger.LogInformation("Hardware button change detected: {SinkName} -> Volume={Volume}%, Muted={Muted}",
                sinkName, volume, muted);

            // Find player(s) using this sink and apply changes
            await ApplyChangeToPlayersAsync(sinkName, volume, muted);
        }
        finally
        {
            // Clear flag after grace period
            _ = Task.Delay(ProcessingGracePeriod).ContinueWith(_ =>
            {
                state.IsProcessingChange = false;
            });
        }
    }

    private async Task ApplyChangeToPlayersAsync(string sinkName, int volume, bool muted)
    {
        // Get PlayerManagerService from DI (avoid circular dependency)
        using var scope = _serviceProvider.CreateScope();
        var playerManager = scope.ServiceProvider.GetService<PlayerManagerService>();
        if (playerManager == null)
        {
            _logger.LogWarning("PlayerManagerService not available");
            return;
        }

        // Find players using this sink
        var allPlayers = playerManager.GetAllPlayers();
        var matchingPlayers = allPlayers.Players.Where(p =>
            p.Device?.Equals(sinkName, StringComparison.OrdinalIgnoreCase) == true ||
            p.Device?.Contains(sinkName, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (matchingPlayers.Count == 0)
        {
            _logger.LogDebug("No players found using sink {SinkName}", sinkName);
            return;
        }

        foreach (var player in matchingPlayers)
        {
            _logger.LogDebug("Applying hardware change to player {PlayerName}: Volume={Volume}%, Muted={Muted}",
                player.Name, volume, muted);

            try
            {
                // Apply volume change
                await playerManager.ApplyHardwareVolumeChangeAsync(player.Name, volume);

                // Apply mute change
                playerManager.ApplyHardwareMuteChange(player.Name, muted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply hardware change to player {PlayerName}", player.Name);
            }
        }
    }

    /// <summary>
    /// Update state tracking when software changes volume (to prevent feedback loop).
    /// Called by PlayerManagerService when volume is changed via API/MA.
    /// </summary>
    public void NotifySoftwareVolumeChange(string sinkName, int volume)
    {
        if (_deviceStates.TryGetValue(sinkName, out var state))
        {
            state.LastKnownVolume = volume;
            state.LastChangeTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Update state tracking when software changes mute (to prevent feedback loop).
    /// Called by PlayerManagerService when mute is changed via API/MA.
    /// </summary>
    public void NotifySoftwareMuteChange(string sinkName, bool muted)
    {
        if (_deviceStates.TryGetValue(sinkName, out var state))
        {
            state.LastKnownMuted = muted;
            state.LastChangeTime = DateTime.UtcNow;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _sinkEventService.SinkChanged -= OnSinkChanged;

        // Unload all modules
        foreach (var (sinkName, state) in _deviceStates)
        {
            if (state.ModuleIndex.HasValue)
            {
                try
                {
                    await _moduleRunner.UnloadModuleAsync(state.ModuleIndex.Value);
                    _logger.LogDebug("Unloaded module {ModuleIndex} for {SinkName} during disposal",
                        state.ModuleIndex.Value, sinkName);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error unloading module during disposal");
                }
            }
        }

        _deviceStates.Clear();
        _initLock.Dispose();
    }
}
