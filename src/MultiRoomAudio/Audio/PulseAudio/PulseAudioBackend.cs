using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;
using Sendspin.SDK.Audio;

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// PulseAudio audio backend implementation.
/// Provides device enumeration, player creation, and volume control for PulseAudio sinks.
/// </summary>
public class PulseAudioBackend : IBackend
{
    private readonly ILogger<PulseAudioBackend> _logger;
    private readonly VolumeCommandRunner _volumeRunner;

    public string Name => "pulse";

    public PulseAudioBackend(
        ILogger<PulseAudioBackend> logger,
        VolumeCommandRunner volumeRunner)
    {
        _logger = logger;
        _volumeRunner = volumeRunner;

        // Configure the device enumerator with a logger
        PulseAudioDeviceEnumerator.SetLogger(logger);
    }

    public IEnumerable<AudioDevice> GetOutputDevices()
    {
        return PulseAudioDeviceEnumerator.GetOutputDevices();
    }

    public AudioDevice? GetDevice(string deviceId)
    {
        return PulseAudioDeviceEnumerator.GetDevice(deviceId);
    }

    public AudioDevice? GetDefaultDevice()
    {
        return PulseAudioDeviceEnumerator.GetDefaultDevice();
    }

    public bool ValidateDevice(string? deviceId, out string? errorMessage)
    {
        return PulseAudioDeviceEnumerator.ValidateDevice(deviceId, out errorMessage);
    }

    public void RefreshDevices()
    {
        PulseAudioDeviceEnumerator.RefreshDevices();
    }

    public DeviceCapabilities? GetDeviceCapabilities(string? deviceId)
    {
        // PulseAudio doesn't expose detailed device capabilities directly.
        // Return default high-res support as PulseAudio handles resampling internally.
        _logger.LogDebug("PulseAudio capability query for sink: {Sink} (returning defaults)", deviceId ?? "default");

        return new DeviceCapabilities(
            SupportedSampleRates: new[] { 44100, 48000, 88200, 96000, 176400, 192000 },
            SupportedBitDepths: new[] { 16, 24, 32 },
            MaxChannels: 2,
            PreferredSampleRate: 192000,
            PreferredBitDepth: 24
        );
    }

    public IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory)
    {
        return CreatePlayer(deviceId, loggerFactory, null);
    }

    public IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory, AudioOutputFormat? outputFormat)
    {
        _logger.LogDebug("Creating PulseAudio player for sink: {Sink}, format: {Format}",
            deviceId ?? "default",
            outputFormat != null ? $"{outputFormat.SampleRate}Hz/{outputFormat.BitDepth}-bit" : "default");

        return new PulseAudioPlayer(
            loggerFactory.CreateLogger<PulseAudioPlayer>(),
            deviceId,
            outputFormat);
    }

    public async Task<bool> SetVolumeAsync(string? deviceId, int volume, CancellationToken cancellationToken = default)
    {
        return await _volumeRunner.SetVolumeAsync(deviceId, volume, cancellationToken);
    }
}
