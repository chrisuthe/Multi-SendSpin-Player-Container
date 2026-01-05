using Microsoft.Extensions.Logging;
using MultiRoomAudio.Models;
using PortAudioSharp;

namespace MultiRoomAudio.Audio;

/// <summary>
/// Enumerates available audio output devices using PortAudio.
/// Device indices match those used by the CLI sendspin tool.
/// </summary>
public static class PortAudioDeviceEnumerator
{
    private static bool _initialized;
    private static readonly object _initLock = new();
    private static ILogger? _logger;

    /// <summary>
    /// Configures the logger for device enumeration diagnostics.
    /// </summary>
    /// <param name="logger">The logger instance to use for diagnostic output.</param>
    public static void SetLogger(ILogger? logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets all available audio output devices.
    /// </summary>
    public static IEnumerable<AudioDevice> GetOutputDevices()
    {
        EnsureInitialized();

        var devices = new List<AudioDevice>();
        var defaultIndex = PortAudio.DefaultOutputDevice;

        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            try
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxOutputChannels > 0)
                {
                    devices.Add(new AudioDevice(
                        Id: i.ToString(),
                        Index: i,
                        Name: info.name,
                        MaxChannels: info.maxOutputChannels,
                        DefaultSampleRate: (int)info.defaultSampleRate,
                        DefaultLowLatencyMs: (int)(info.defaultLowOutputLatency * 1000),
                        DefaultHighLatencyMs: (int)(info.defaultHighOutputLatency * 1000),
                        IsDefault: i == defaultIndex
                    ));
                }
            }
            catch (Exception ex)
            {
                // Skip devices that can't be queried - this is expected behavior for
                // devices that are in use, disconnected, or have driver issues.
                // We log the error for diagnostic purposes but continue enumeration
                // to return all available devices.
                _logger?.LogDebug(ex, "Skipping device {DeviceIndex} - could not query device info: {Message}",
                    i, ex.Message);
            }
        }

        return devices;
    }

    /// <summary>
    /// Gets a specific audio device by index or name.
    /// </summary>
    public static AudioDevice? GetDevice(string deviceId)
    {
        EnsureInitialized();

        // Try to parse as index
        if (int.TryParse(deviceId, out var index))
        {
            return GetOutputDevices().FirstOrDefault(d => d.Index == index);
        }

        // Search by name (partial match)
        return GetOutputDevices()
            .FirstOrDefault(d => d.Name.Contains(deviceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the default audio output device.
    /// </summary>
    public static AudioDevice? GetDefaultDevice()
    {
        return GetOutputDevices().FirstOrDefault(d => d.IsDefault);
    }

    /// <summary>
    /// Validates that a device ID exists and is usable.
    /// </summary>
    public static bool ValidateDevice(string? deviceId, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            // Default device is always valid
            errorMessage = null;
            return true;
        }

        var device = GetDevice(deviceId);
        if (device == null)
        {
            errorMessage = $"Device '{deviceId}' not found. Use /api/devices to list available devices.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Refreshes device list (re-enumerates hardware).
    /// </summary>
    public static void RefreshDevices()
    {
        lock (_initLock)
        {
            if (_initialized)
            {
                try
                {
                    PortAudio.Terminate();
                }
                catch (Exception)
                {
                    // Ignore termination errors - PortAudio may already be terminated
                    // or in an invalid state. This is a cleanup operation.
                }
                _initialized = false;
            }
        }
    }

    private static void EnsureInitialized()
    {
        lock (_initLock)
        {
            if (!_initialized)
            {
                // NOTE: We intentionally do NOT call PortAudio.Terminate() after initialization.
                // PortAudio is designed to be initialized once and remain active for the lifetime
                // of the process. Calling Terminate() and then re-initializing breaks device
                // enumeration on subsequent calls, particularly on Linux/ALSA systems where the
                // audio subsystem state becomes inconsistent. This is a known PortAudio behavior,
                // not a resource leak - the library handles cleanup on process exit.
                PortAudio.Initialize();
                _initialized = true;
            }
        }
    }
}
