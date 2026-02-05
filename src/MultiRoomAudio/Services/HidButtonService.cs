using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages HID button support for USB audio devices.
/// Reads HID events directly from /dev/input/eventX and applies volume/mute changes to players.
/// </summary>
public class HidButtonService : IAsyncDisposable
{
    private readonly ILogger<HidButtonService> _logger;
    private readonly HidInputDeviceDetector _hidDetector;
    private readonly ConfigurationService _configService;
    private readonly IServiceProvider _serviceProvider;

    private readonly ConcurrentDictionary<string, HidButtonDeviceState> _deviceStates = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Volume step for volume up/down buttons (percentage).
    /// </summary>
    private const int VolumeStep = 5;

    public HidButtonService(
        ILogger<HidButtonService> logger,
        HidInputDeviceDetector hidDetector,
        ConfigurationService configService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _hidDetector = hidDetector;
        _configService = configService;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Initialize the HID button service.
    /// Note: Actual HID event reading starts when a player is created for a device with HID enabled.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _initLock.Wait(cancellationToken);
        try
        {
            if (_initialized)
                return Task.CompletedTask;

            _logger.LogInformation("HID button service initialized (direct event reading mode)");
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }

        return Task.CompletedTask;
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
            var serial = device.Identifiers?.Serial;
            inputDevice = _hidDetector.FindInputDevice(busPath, vendorId, productId, serial);
        }

        return new HidButtonStatusResponse(
            DeviceId: sinkName,
            HidButtonsAvailable: !string.IsNullOrEmpty(inputDevice),
            HidButtonsEnabled: state?.Enabled ?? false,
            InputDevicePath: inputDevice,
            ModuleIndex: null, // No longer using PA modules
            ErrorMessage: null
        );
    }

    /// <summary>
    /// Enable HID button support for a device.
    /// This finds the input device and saves the configuration.
    /// Actual event reading starts when StartHidReaderForPlayer is called.
    /// </summary>
    public Task<HidButtonEnableResponse> EnableHidButtonsAsync(
        AudioDevice device,
        CancellationToken cancellationToken = default)
    {
        var sinkName = device.Id;
        var busPath = device.Identifiers?.BusPath;
        var vendorId = device.Identifiers?.VendorId;
        var productId = device.Identifiers?.ProductId;

        _logger.LogInformation("Enabling HID buttons for device {SinkName}", sinkName);

        // Find HID input device
        var serial = device.Identifiers?.Serial;
        var inputDevice = _hidDetector.FindInputDevice(busPath, vendorId, productId, serial);
        if (string.IsNullOrEmpty(inputDevice))
        {
            _logger.LogWarning("No HID input device found for {SinkName}", sinkName);
            return Task.FromResult(new HidButtonEnableResponse(
                Success: false,
                DeviceId: sinkName,
                HidButtonsEnabled: false,
                InputDevicePath: null,
                ModuleIndex: null,
                Message: "No HID input device found. This device may not have hardware volume/mute buttons."
            ));
        }

        // Create/update state (reader will be started when player is associated)
        var state = _deviceStates.GetOrAdd(sinkName, _ => new HidButtonDeviceState());
        state.SinkName = sinkName;
        state.Enabled = true;
        state.InputDevicePath = inputDevice;

        // Save configuration
        var deviceKey = ConfigurationService.GenerateDeviceKey(device);
        SaveHidButtonConfig(deviceKey, true, inputDevice, device);

        _logger.LogInformation("HID buttons enabled for {SinkName} (input: {InputDevice})", sinkName, inputDevice);

        return Task.FromResult(new HidButtonEnableResponse(
            Success: true,
            DeviceId: sinkName,
            HidButtonsEnabled: true,
            InputDevicePath: inputDevice,
            ModuleIndex: null,
            Message: "HID button support enabled. Hardware volume/mute buttons will control the player."
        ));
    }

    /// <summary>
    /// Disable HID button support for a device.
    /// </summary>
    public Task<HidButtonEnableResponse> DisableHidButtonsAsync(
        AudioDevice device,
        CancellationToken cancellationToken = default)
    {
        var sinkName = device.Id;

        _logger.LogInformation("Disabling HID buttons for device {SinkName}", sinkName);

        if (_deviceStates.TryRemove(sinkName, out var state))
        {
            // Stop the reader task
            StopHidReader(state);
        }

        // Save configuration
        var deviceKey = ConfigurationService.GenerateDeviceKey(device);
        SaveHidButtonConfig(deviceKey, false, null, device);

        _logger.LogInformation("HID buttons disabled for {SinkName}", sinkName);

        return Task.FromResult(new HidButtonEnableResponse(
            Success: true,
            DeviceId: sinkName,
            HidButtonsEnabled: false,
            InputDevicePath: null,
            ModuleIndex: null,
            Message: "HID button support disabled."
        ));
    }

    /// <summary>
    /// Start HID event reading for a player.
    /// Called by PlayerManagerService when a player is created/started.
    /// </summary>
    public void StartHidReaderForPlayer(string sinkName, string playerName)
    {
        // Check if HID is enabled for this device
        var deviceConfig = FindDeviceConfigBySinkName(sinkName);
        if (deviceConfig?.HidButtons?.Enabled != true)
        {
            _logger.LogDebug("HID buttons not enabled for {SinkName}", sinkName);
            return;
        }

        var inputDevice = deviceConfig.HidButtons.LastKnownInputPath;
        if (string.IsNullOrEmpty(inputDevice))
        {
            _logger.LogDebug("No input device path for {SinkName}", sinkName);
            return;
        }

        // Get or create state
        var state = _deviceStates.GetOrAdd(sinkName, _ => new HidButtonDeviceState
        {
            SinkName = sinkName,
            Enabled = true,
            InputDevicePath = inputDevice
        });

        // If already running for this player, skip
        if (state.ReaderTask != null && !state.ReaderTask.IsCompleted && state.PlayerName == playerName)
        {
            _logger.LogDebug("HID reader already running for {SinkName} / {PlayerName}", sinkName, playerName);
            return;
        }

        // Stop any existing reader
        StopHidReader(state);

        // Start new reader
        state.PlayerName = playerName;
        state.ReaderCts = new CancellationTokenSource();
        state.ReaderTask = Task.Run(() => ReadHidEventsAsync(state, state.ReaderCts.Token));

        _logger.LogInformation("Started HID event reader for {SinkName} -> player '{PlayerName}' (input: {InputDevice})",
            sinkName, playerName, inputDevice);
    }

    /// <summary>
    /// Stop HID event reading for a player.
    /// Called by PlayerManagerService when a player is stopped/deleted.
    /// </summary>
    public void StopHidReaderForPlayer(string sinkName, string playerName)
    {
        if (_deviceStates.TryGetValue(sinkName, out var state) && state.PlayerName == playerName)
        {
            StopHidReader(state);
            _logger.LogInformation("Stopped HID event reader for {SinkName} / {PlayerName}", sinkName, playerName);
        }
    }

    /// <summary>
    /// Check if HID buttons are enabled for a device (by sink name).
    /// </summary>
    public bool IsHidEnabledForDevice(string sinkName)
    {
        var deviceConfig = FindDeviceConfigBySinkName(sinkName);
        return deviceConfig?.HidButtons?.Enabled == true;
    }

    private DeviceConfiguration? FindDeviceConfigBySinkName(string sinkName)
    {
        var allConfigs = _configService.GetAllDeviceConfigurations();
        foreach (var (_, config) in allConfigs)
        {
            if (config.LastKnownSinkName?.Equals(sinkName, StringComparison.OrdinalIgnoreCase) == true)
            {
                return config;
            }
        }
        return null;
    }

    private void StopHidReader(HidButtonDeviceState state)
    {
        if (state.ReaderCts != null)
        {
            state.ReaderCts.Cancel();
            state.ReaderCts.Dispose();
            state.ReaderCts = null;
        }
        state.ReaderTask = null;
        state.PlayerName = null;
    }

    private async Task ReadHidEventsAsync(HidButtonDeviceState state, CancellationToken ct)
    {
        var inputDevice = state.InputDevicePath;
        var playerName = state.PlayerName;

        if (string.IsNullOrEmpty(inputDevice) || string.IsNullOrEmpty(playerName))
        {
            _logger.LogWarning("Cannot start HID reader: missing input device or player name");
            return;
        }

        _logger.LogDebug("Opening HID input device: {InputDevice}", inputDevice);

        try
        {
            using var fs = new FileStream(inputDevice, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[LinuxInputConstants.InputEventSize];

            while (!ct.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (bytesRead < LinuxInputConstants.InputEventSize)
                    continue;

                var evt = ParseInputEvent(buffer);

                // Log ALL events for debugging (to diagnose devices with non-standard key codes)
                // EV_SYN(0) events are too noisy, skip those
                if (evt.Type != LinuxInputConstants.EV_SYN)
                {
                    _logger.LogDebug("HID event: type={Type} code={Code} value={Value} (device: {Device})",
                        evt.Type, evt.Code, evt.Value, inputDevice);
                }

                // Only process key press events (not release or repeat)
                if (evt.Type != LinuxInputConstants.EV_KEY || evt.Value != LinuxInputConstants.KEY_PRESSED)
                    continue;

                await HandleKeyEventAsync(evt.Code, state, playerName);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("HID reader cancelled for {InputDevice}", inputDevice);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("HID input device not found: {InputDevice}. Device may have been unplugged.", inputDevice);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogError("Permission denied reading HID device {InputDevice}. Ensure the container has access to /dev/input devices.", inputDevice);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading HID events from {InputDevice}", inputDevice);
        }
    }

    private static LinuxInputEvent ParseInputEvent(byte[] buffer)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<LinuxInputEvent>(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private async Task HandleKeyEventAsync(ushort keyCode, HidButtonDeviceState state, string playerName)
    {
        // Get PlayerManagerService from DI
        using var scope = _serviceProvider.CreateScope();
        var playerManager = scope.ServiceProvider.GetService<PlayerManagerService>();
        if (playerManager == null)
        {
            _logger.LogWarning("PlayerManagerService not available");
            return;
        }

        switch (keyCode)
        {
            case LinuxInputConstants.KEY_MUTE:
                // Get current mute state from player (not our cached state, which may be stale)
                var allPlayers = playerManager.GetAllPlayers();
                var player = allPlayers.Players.FirstOrDefault(p =>
                    p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));

                if (player == null)
                {
                    _logger.LogWarning("Player '{PlayerName}' not found for mute toggle", playerName);
                    return;
                }

                // Toggle based on actual player state
                var newMuteState = !player.IsMuted;
                state.IsMuted = newMuteState; // Keep our cache in sync

                _logger.LogInformation("HID mute button pressed for player '{PlayerName}': {State} (was {OldState})",
                    playerName, newMuteState ? "muted" : "unmuted", player.IsMuted ? "muted" : "unmuted");
                playerManager.SetMuted(playerName, newMuteState);
                break;

            case LinuxInputConstants.KEY_VOLUMEUP:
                _logger.LogDebug("HID volume up pressed for player '{PlayerName}'", playerName);
                await AdjustVolumeAsync(playerManager, playerName, VolumeStep);
                break;

            case LinuxInputConstants.KEY_VOLUMEDOWN:
                _logger.LogDebug("HID volume down pressed for player '{PlayerName}'", playerName);
                await AdjustVolumeAsync(playerManager, playerName, -VolumeStep);
                break;

            default:
                _logger.LogDebug("Unhandled HID key code: {KeyCode}", keyCode);
                break;
        }
    }

    private async Task AdjustVolumeAsync(PlayerManagerService playerManager, string playerName, int delta)
    {
        var allPlayers = playerManager.GetAllPlayers();
        var player = allPlayers.Players.FirstOrDefault(p =>
            p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));

        if (player == null)
        {
            _logger.LogWarning("Player '{PlayerName}' not found for volume adjustment", playerName);
            return;
        }

        var newVolume = Math.Clamp(player.Volume + delta, 0, 100);
        _logger.LogInformation("HID volume {Direction} for player '{PlayerName}': {OldVol}% -> {NewVol}%",
            delta > 0 ? "up" : "down", playerName, player.Volume, newVolume);

        await playerManager.SetVolumeAsync(playerName, newVolume);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Stop all HID readers
        foreach (var (sinkName, state) in _deviceStates)
        {
            StopHidReader(state);
            _logger.LogDebug("Stopped HID reader for {SinkName} during disposal", sinkName);
        }

        _deviceStates.Clear();
        _initLock.Dispose();

        await Task.CompletedTask;
    }
}
