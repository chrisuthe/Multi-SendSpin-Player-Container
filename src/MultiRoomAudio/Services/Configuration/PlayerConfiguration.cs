namespace MultiRoomAudio.Services.Configuration;

/// <summary>
/// Configuration for a single player.
/// Matches the YAML format from the Python implementation for backward compatibility.
/// </summary>
public class PlayerConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string Provider { get; set; } = "sendspin";
    public bool Autostart { get; set; } = true;

    /// <summary>
    /// Whether to automatically resume playback when the audio device is reconnected.
    /// The player always restarts when its device reappears, but this controls whether
    /// playback resumes automatically or not.
    /// When enabled: Player restarts and resumes playing where it left off.
    /// When disabled: Player restarts but stays paused/stopped.
    /// </summary>
    public bool AutoResume { get; set; } = false;

    public int DelayMs { get; set; } = 0;
    public string? Server { get; set; }
    public int? Volume { get; set; }

    // PortAudio device index (for Sendspin SDK)
    public int? PortAudioDeviceIndex { get; set; }

    // Advertised audio format (for advanced formats feature)
    public string? AdvertisedFormat { get; set; }

    // Buffer size in milliseconds (for audio pipeline tuning)
    public int BufferSizeMs { get; set; } = 100;

    // Additional provider-specific settings
    public Dictionary<string, object>? Extra { get; set; }
}
