using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Models.LogModels;

namespace MultiRoomAudio.Hubs;

/// <summary>
/// Extension methods for broadcasting log entries.
/// </summary>
public static class LogStreamHubExtensions
{
    /// <summary>
    /// Broadcasts a log entry to all connected clients in the "all" group.
    /// </summary>
    public static async Task BroadcastLogEntryAsync(
        this IHubContext<LogStreamHub> hubContext,
        LogEntryDto entry)
    {
        await hubContext.Clients.Group("all").SendAsync("LogEntry", entry);
    }
}
