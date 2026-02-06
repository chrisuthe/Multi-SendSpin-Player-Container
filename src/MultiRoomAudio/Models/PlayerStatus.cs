namespace MultiRoomAudio.Models;

/// <summary>
/// Player state enumeration.
/// </summary>
public enum PlayerState
{
    Created,
    Starting,
    Connecting,
    Connected,
    Buffering,
    Playing,
    Paused,
    Stopped,
    Error,
    Reconnecting,
    WaitingForServer,
    /// <summary>
    /// Player stopped due to audio device loss (USB unplug).
    /// Waiting for the device to reconnect.
    /// </summary>
    WaitingForDevice
}

/// <summary>
/// Response containing player status.
/// </summary>
public record PlayerResponse(
    string Name,
    PlayerState State,
    string? Device,
    string ClientId,
    string? ServerUrl,
    string? ServerName,        // Friendly name from MA (e.g., "Music Assistant")
    string? ConnectedAddress,  // IP:port we connected to (e.g., "192.168.1.50:8095")
    int Volume,
    int StartupVolume,
    bool IsMuted,
    int DelayMs,
    int OutputLatencyMs,
    DateTime CreatedAt,
    DateTime? ConnectedAt,
    string? ErrorMessage,
    bool IsClockSynced,
    PlayerMetrics? Metrics,
    DeviceCapabilities? DeviceCapabilities = null,
    bool IsPendingReconnection = false,
    bool AutoResume = false,
    int? ReconnectionAttempts = null,
    DateTime? NextReconnectionAttempt = null,
    string? AdvertisedFormat = null,
    TrackInfo? CurrentTrack = null
);

/// <summary>
/// Player metrics for monitoring.
/// </summary>
public record PlayerMetrics(
    int BufferLevel,
    int BufferCapacity,
    long SamplesPlayed,
    long Underruns,
    long Overruns
);

/// <summary>
/// Current track information from Music Assistant.
/// </summary>
public record TrackInfo(
    string? Title,
    string? Artist,
    string? Album,
    string? ArtworkUrl,
    double? DurationSeconds,
    double? PositionSeconds
);

/// <summary>
/// List response wrapper.
/// </summary>
public record PlayersListResponse(
    List<PlayerResponse> Players,
    int Count
);
