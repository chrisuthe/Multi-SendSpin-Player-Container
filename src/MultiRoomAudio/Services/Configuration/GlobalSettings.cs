namespace MultiRoomAudio.Services.Configuration;

/// <summary>
/// Global application settings persisted in settings.yaml.
/// </summary>
public class GlobalSettings
{
    public int BufferSeconds { get; set; } = 30;
}
