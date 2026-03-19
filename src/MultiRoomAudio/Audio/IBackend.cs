using MultiRoomAudio.Models.DeviceInfo;
using Sendspin.SDK.Audio;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Common interface for audio backends.
/// Each backend provides device enumeration, player creation, and volume control.
/// </summary>
public interface IBackend
{
    /// <summary>
    /// Backend name identifier (e.g., "pulse").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets all available audio output devices.
    /// </summary>
    IEnumerable<AudioDevice> GetOutputDevices();

    /// <summary>
    /// Gets a specific audio device by ID or index.
    /// </summary>
    /// <param name="deviceId">Device ID or index as string.</param>
    /// <returns>The device if found, null otherwise.</returns>
    AudioDevice? GetDevice(string deviceId);

    /// <summary>
    /// Gets the default audio output device.
    /// </summary>
    AudioDevice? GetDefaultDevice();

    /// <summary>
    /// Gets device capabilities (supported sample rates, bit depths).
    /// </summary>
    /// <param name="deviceId">Device ID or null for default.</param>
    /// <returns>Device capabilities, or null if probing failed.</returns>
    DeviceCapabilities? GetDeviceCapabilities(string? deviceId);

    /// <summary>
    /// Validates that a device ID exists and is usable.
    /// </summary>
    /// <param name="deviceId">Device ID to validate (null means default).</param>
    /// <param name="errorMessage">Error message if validation fails.</param>
    /// <returns>True if the device is valid.</returns>
    bool ValidateDevice(string? deviceId, out string? errorMessage);

    /// <summary>
    /// Refreshes the device list (re-enumerates hardware).
    /// </summary>
    void RefreshDevices();

    /// <summary>
    /// Creates an audio player for the specified device.
    /// PulseAudio handles all format conversion natively (always float32 input).
    /// </summary>
    /// <param name="deviceId">Device ID or null for default.</param>
    /// <param name="loggerFactory">Logger factory for diagnostics.</param>
    /// <returns>A new audio player instance.</returns>
    IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory);

    /// <summary>
    /// Sets hardware volume for a device.
    /// </summary>
    /// <param name="deviceId">Device ID or null for default.</param>
    /// <param name="volume">Volume percentage (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if hardware volume was set, false if only software volume is available.</returns>
    Task<bool> SetVolumeAsync(string? deviceId, int volume, CancellationToken cancellationToken = default);
}
