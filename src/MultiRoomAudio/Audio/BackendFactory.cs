using MultiRoomAudio.Audio.Mock;
using MultiRoomAudio.Audio.PulseAudio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Factory for creating the PulseAudio audio backend.
/// </summary>
/// <remarks>
/// Both HAOS and Docker standalone modes use PulseAudio for consistent behavior.
/// </remarks>
public class BackendFactory
{
    private readonly ILogger<BackendFactory> _logger;
    private readonly IBackend _backend;

    /// <summary>
    /// The active audio backend.
    /// </summary>
    public IBackend Backend => _backend;

    /// <summary>
    /// Name of the active backend (always "pulse").
    /// </summary>
    public string BackendName => _backend.Name;

    public BackendFactory(
        ILogger<BackendFactory> logger,
        EnvironmentService environment,
        ILoggerFactory loggerFactory,
        Utilities.VolumeCommandRunner volumeRunner,
        CustomSinksService? customSinksService = null,
        MockHardwareConfigService? mockConfigService = null)
    {
        _logger = logger;

        if (environment.IsMockHardware)
        {
            _logger.LogInformation("Initializing mock audio backend (MOCK_HARDWARE mode)");
            _backend = new MockAudioBackend(
                loggerFactory.CreateLogger<MockAudioBackend>(),
                customSinksService,
                mockConfigService);
        }
        else
        {
            _logger.LogInformation("Initializing PulseAudio backend");
            _backend = new PulseAudioBackend(
                loggerFactory.CreateLogger<PulseAudioBackend>(),
                volumeRunner);
        }

        _logger.LogInformation("Audio backend: {Backend}", _backend.Name);
    }

    /// <summary>
    /// Gets all available audio output devices from the active backend.
    /// </summary>
    public IEnumerable<AudioDevice> GetOutputDevices()
    {
        return _backend.GetOutputDevices();
    }

    /// <summary>
    /// Gets a specific audio device by ID.
    /// </summary>
    public AudioDevice? GetDevice(string deviceId)
    {
        return _backend.GetDevice(deviceId);
    }

    /// <summary>
    /// Gets the default audio output device.
    /// </summary>
    public AudioDevice? GetDefaultDevice()
    {
        return _backend.GetDefaultDevice();
    }

    /// <summary>
    /// Validates that a device ID exists and is usable.
    /// </summary>
    public bool ValidateDevice(string? deviceId, out string? errorMessage)
    {
        return _backend.ValidateDevice(deviceId, out errorMessage);
    }

    /// <summary>
    /// Refreshes the device list.
    /// </summary>
    public void RefreshDevices()
    {
        _backend.RefreshDevices();
    }

    /// <summary>
    /// Gets device capabilities (supported sample rates, bit depths).
    /// </summary>
    public DeviceCapabilities? GetDeviceCapabilities(string? deviceId)
    {
        return _backend.GetDeviceCapabilities(deviceId);
    }

    /// <summary>
    /// Creates an audio player for the specified device.
    /// PulseAudio handles all format conversion natively (always float32 input).
    /// </summary>
    public Sendspin.SDK.Audio.IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory)
    {
        return _backend.CreatePlayer(deviceId, loggerFactory);
    }

    /// <summary>
    /// Sets hardware volume for a device.
    /// </summary>
    public Task<bool> SetVolumeAsync(string? deviceId, int volume, CancellationToken cancellationToken = default)
    {
        return _backend.SetVolumeAsync(deviceId, volume, cancellationToken);
    }
}
