using MultiRoomAudio.Models.DeviceInfo;

namespace MultiRoomAudio.Models.PlayerStatus;

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
