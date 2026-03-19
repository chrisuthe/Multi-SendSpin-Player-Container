using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Models.PlayerStatus;

namespace MultiRoomAudio.Hubs;

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
