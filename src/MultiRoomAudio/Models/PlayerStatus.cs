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
    Error
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
    int Volume,
    bool IsMuted,
    int DelayMs,
    int OutputLatencyMs,
    DateTime CreatedAt,
    DateTime? ConnectedAt,
    string? ErrorMessage,
    bool IsClockSynced,
    PlayerMetrics? Metrics,
    AudioOutputFormat? OutputFormat = null,
    DeviceCapabilities? DeviceCapabilities = null
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
/// List response wrapper.
/// </summary>
public record PlayersListResponse(
    List<PlayerResponse> Players,
    int Count
);
