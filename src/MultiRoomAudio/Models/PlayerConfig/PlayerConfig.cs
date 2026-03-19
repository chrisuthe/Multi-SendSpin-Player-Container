namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Player configuration stored in memory.
/// </summary>
public class PlayerConfig
{
    public required string Name { get; set; }
    public required string ClientId { get; set; }
    public string? DeviceId { get; set; }
    public string? ServerUrl { get; set; }
    public int DelayMs { get; set; }
    public string LogLevel { get; set; } = "INFO";
    public string Codec { get; set; } = "opus";
    public int BufferSizeMs { get; set; } = 100;
    public int Volume { get; set; } = 75;
    public bool IsMuted { get; set; }
    public string? AdvertisedFormat { get; set; }
}
