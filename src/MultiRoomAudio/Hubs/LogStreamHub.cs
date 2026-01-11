using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Hubs;

/// <summary>
/// SignalR hub for real-time log streaming.
/// Clients can connect to receive live log entries as they are written.
/// </summary>
public class LogStreamHub : Hub
{
    private readonly ILogger<LogStreamHub> _logger;
    private readonly LoggingService _loggingService;

    public LogStreamHub(
        ILogger<LogStreamHub> logger,
        LoggingService loggingService)
    {
        _logger = logger;
        _loggingService = loggingService;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Log stream client connected: {ConnectionId}", Context.ConnectionId);

        // Add to "all" group by default
        await Groups.AddToGroupAsync(Context.ConnectionId, "all");

        // Send recent logs to newly connected client
        var recentLogs = _loggingService.GetEntries(new LogQueryOptions(Take: 50, NewestFirst: true));
        var dtos = recentLogs.Reverse().Select(e => e.ToDto()).ToList();
        await Clients.Caller.SendAsync("InitialLogs", dtos);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Log stream client disconnected: {ConnectionId}, Error: {Error}",
            Context.ConnectionId, exception?.Message);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Allows clients to subscribe to specific log levels or categories.
    /// </summary>
    public async Task Subscribe(string? level, string? category)
    {
        if (!string.IsNullOrEmpty(level))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"level:{level.ToLowerInvariant()}");
        }

        if (!string.IsNullOrEmpty(category))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"category:{category}");
        }
    }

    /// <summary>
    /// Removes subscription from specific groups.
    /// </summary>
    public async Task Unsubscribe(string? level, string? category)
    {
        if (!string.IsNullOrEmpty(level))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"level:{level.ToLowerInvariant()}");
        }

        if (!string.IsNullOrEmpty(category))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"category:{category}");
        }
    }
}

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
