namespace MultiRoomAudio.Services.Logging;

/// <summary>
/// Log category for filtering and organization.
/// </summary>
public enum LogCategory
{
    System,     // Startup, shutdown, environment
    Player,     // Player create, connect, play, stop, delete
    Audio,      // PulseAudio, volume control, underflows
    API,        // HTTP requests/responses
    Config,     // Configuration load/save/CRUD
    SDK,        // SDK interactions, connection state, sync
    Trigger     // Relay boards, 12V triggers, channel operations
}
