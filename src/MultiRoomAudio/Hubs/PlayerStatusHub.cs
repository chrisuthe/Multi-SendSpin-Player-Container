using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Hubs;

/// <summary>
/// SignalR hub for real-time player status updates.
/// Clients can connect to receive live player state changes.
/// </summary>
/// <remarks>
/// Response Structure: All status updates wrap the players array in an object: { players: [...] }
/// This matches the frontend JavaScript expectation in wwwroot/js/app.js (line ~58) where
/// the handler accesses data.players. Do not simplify to send the array directly.
/// </remarks>
public class PlayerStatusHub : Hub
{
    private readonly ILogger<PlayerStatusHub> _logger;
    private readonly PlayerManagerService _playerManager;
    private readonly StartupProgressService _startupProgress;

    public PlayerStatusHub(
        ILogger<PlayerStatusHub> logger,
        PlayerManagerService playerManager,
        StartupProgressService startupProgress)
    {
        _logger = logger;
        _playerManager = playerManager;
        _startupProgress = startupProgress;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Client connected: {ConnectionId}", Context.ConnectionId);

        // If startup is still in progress, send current progress first
        if (!_startupProgress.IsStartupComplete)
        {
            await Clients.Caller.SendAsync("StartupProgress", _startupProgress.GetProgress());
        }

        // Send current state to newly connected client
        var players = _playerManager.GetAllPlayers();
        await Clients.Caller.SendAsync("PlayerStatusUpdate", new { players = players.Players });

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Client disconnected: {ConnectionId}, Error: {Error}",
            Context.ConnectionId, exception?.Message);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Clients can request current status on demand.
    /// </summary>
    public async Task RequestStatus()
    {
        var players = _playerManager.GetAllPlayers();
        await Clients.Caller.SendAsync("PlayerStatusUpdate", new { players = players.Players });
    }
}

/// <summary>
/// Extension methods for broadcasting player status updates.
/// </summary>
public static class PlayerStatusHubExtensions
{
    /// <summary>
    /// Broadcasts a player status update to all connected clients.
    /// </summary>
    public static async Task BroadcastStatusUpdateAsync(
        this IHubContext<PlayerStatusHub> hubContext,
        PlayersListResponse players)
    {
        await hubContext.Clients.All.SendAsync("PlayerStatusUpdate", new { players = players.Players });
    }

    /// <summary>
    /// Notifies all connected clients that the device list has changed.
    /// Clients should refresh their device lists via the API.
    /// </summary>
    public static async Task BroadcastDeviceListChangedAsync(
        this IHubContext<PlayerStatusHub> hubContext)
    {
        await hubContext.Clients.All.SendAsync("DeviceListChanged");
    }
}
