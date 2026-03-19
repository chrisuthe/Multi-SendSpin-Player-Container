using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Services;
using MultiRoomAudio.Services.StartupProgress;

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
