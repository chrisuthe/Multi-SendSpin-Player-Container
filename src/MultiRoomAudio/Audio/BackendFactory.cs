using MultiRoomAudio.Audio.Alsa;
using MultiRoomAudio.Audio.PulseAudio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Factory for creating the appropriate audio backend based on the runtime environment.
/// </summary>
/// <remarks>
/// - HAOS mode: Uses PulseAudio (containers don't have direct ALSA access)
/// - Docker standalone mode: Uses ALSA (direct hardware access, supports software-defined devices)
/// </remarks>
public class BackendFactory
{
    private readonly ILogger<BackendFactory> _logger;
    private readonly IBackend _backend;
    private readonly EnvironmentService _environment;

    /// <summary>
    /// The active audio backend.
    /// </summary>
    public IBackend Backend => _backend;

    /// <summary>
    /// Name of the active backend ("alsa" or "pulse").
    /// </summary>
    public string BackendName => _backend.Name;

    /// <summary>
    /// Whether ALSA backend is in use.
    /// </summary>
    public bool IsAlsaBackend => _backend.Name == "alsa";

    /// <summary>
    /// Whether PulseAudio backend is in use.
    /// </summary>
    public bool IsPulseBackend => _backend.Name == "pulse";

    public BackendFactory(
        ILogger<BackendFactory> logger,
        EnvironmentService environment,
        ILoggerFactory loggerFactory,
        Utilities.VolumeCommandRunner volumeRunner)
    {
        _logger = logger;
        _environment = environment;

        if (environment.UseAlsaBackend)
        {
            _logger.LogInformation("Initializing ALSA audio backend for Docker mode");
            _backend = new AlsaBackend(loggerFactory.CreateLogger<AlsaBackend>());
        }
        else
        {
            _logger.LogInformation("Initializing PulseAudio backend for HAOS mode");
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
    /// </summary>
    public Sendspin.SDK.Audio.IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory)
    {
        return _backend.CreatePlayer(deviceId, loggerFactory);
    }

    /// <summary>
    /// Creates an audio player with a specific output format.
    /// </summary>
    public Sendspin.SDK.Audio.IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory, AudioOutputFormat? outputFormat)
    {
        return _backend.CreatePlayer(deviceId, loggerFactory, outputFormat);
    }

    /// <summary>
    /// Sets hardware volume for a device.
    /// </summary>
    public Task<bool> SetVolumeAsync(string? deviceId, int volume, CancellationToken cancellationToken = default)
    {
        return _backend.SetVolumeAsync(deviceId, volume, cancellationToken);
    }
}
