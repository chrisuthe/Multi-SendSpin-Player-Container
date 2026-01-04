using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for player management.
/// </summary>
public static class PlayersEndpoint
{
    public static void MapPlayersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/players")
            .WithTags("Players")
            .WithOpenApi();

        // GET /api/players - List all players
        group.MapGet("/", (PlayerManagerService manager, ILogger<PlayerManagerService> logger) =>
        {
            logger.LogDebug("API: GET /api/players");
            var response = manager.GetAllPlayers();
            logger.LogDebug("API: Returning {PlayerCount} players", response.Count);
            return Results.Ok(response);
        })
        .WithName("ListPlayers")
        .WithDescription("Get all active players");

        // GET /api/players/{name} - Get specific player
        group.MapGet("/{name}", (string name, PlayerManagerService manager, ILogger<PlayerManagerService> logger) =>
        {
            logger.LogDebug("API: GET /api/players/{PlayerName}", name);
            var player = manager.GetPlayer(name);
            if (player == null)
            {
                logger.LogDebug("API: Player {PlayerName} not found", name);
                return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
            }

            return Results.Ok(player);
        })
        .WithName("GetPlayer")
        .WithDescription("Get details of a specific player");

        // POST /api/players - Create new player
        group.MapPost("/", async (
            PlayerCreateRequest request,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger,
            CancellationToken ct) =>
        {
            logger.LogDebug("API: POST /api/players - Creating player {PlayerName}", request.Name);
            try
            {
                var player = await manager.CreatePlayerAsync(request, ct);
                logger.LogInformation("API: Player {PlayerName} created successfully", player.Name);
                return Results.Created($"/api/players/{player.Name}", player);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                logger.LogWarning("API: Player creation conflict - {Message}", ex.Message);
                return Results.Conflict(new ErrorResponse(false, ex.Message));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning("API: Player creation bad request - {Message}", ex.Message);
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to create player {PlayerName}", request.Name);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to create player");
            }
        })
        .WithName("CreatePlayer")
        .WithDescription("Create and start a new player");

        // DELETE /api/players/{name} - Stop and remove player (and config)
        group.MapDelete("/{name}", async (
            string name,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger) =>
        {
            logger.LogDebug("API: DELETE /api/players/{PlayerName}", name);
            var deleted = await manager.DeletePlayerAsync(name);
            if (!deleted)
            {
                logger.LogDebug("API: Player {PlayerName} not found for deletion", name);
                return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
            }

            logger.LogInformation("API: Player {PlayerName} deleted successfully", name);
            return Results.Ok(new SuccessResponse(true, $"Player '{name}' deleted"));
        })
        .WithName("DeletePlayer")
        .WithDescription("Stop and remove a player (also removes from config)");

        // POST /api/players/{name}/stop - Stop player (keeps config)
        group.MapPost("/{name}/stop", async (
            string name,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger) =>
        {
            logger.LogDebug("API: POST /api/players/{PlayerName}/stop", name);
            var stopped = await manager.StopPlayerAsync(name);
            if (!stopped)
            {
                logger.LogDebug("API: Player {PlayerName} not found for stop", name);
                return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
            }

            logger.LogInformation("API: Player {PlayerName} stopped successfully", name);
            return Results.Ok(new SuccessResponse(true, $"Player '{name}' stopped"));
        })
        .WithName("StopPlayer")
        .WithDescription("Stop a player (config preserved for restart)");

        // POST /api/players/{name}/restart - Restart player
        group.MapPost("/{name}/restart", async (
            string name,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger,
            CancellationToken ct) =>
        {
            logger.LogDebug("API: POST /api/players/{PlayerName}/restart", name);
            try
            {
                var player = await manager.RestartPlayerAsync(name, ct);
                if (player == null)
                {
                    logger.LogDebug("API: Player {PlayerName} not found for restart", name);
                    return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
                }

                logger.LogInformation("API: Player {PlayerName} restarted successfully", name);
                return Results.Ok(player);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to restart player {PlayerName}", name);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to restart player");
            }
        })
        .WithName("RestartPlayer")
        .WithDescription("Stop and restart a player");

        // PUT /api/players/{name}/device - Switch audio device
        group.MapPut("/{name}/device", async (
            string name,
            DeviceSwitchRequest request,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger,
            CancellationToken ct) =>
        {
            logger.LogDebug("API: PUT /api/players/{PlayerName}/device to {Device}",
                name, request.Device ?? "(default)");
            try
            {
                var success = await manager.SwitchDeviceAsync(name, request.Device, ct);
                if (!success)
                {
                    logger.LogDebug("API: Player {PlayerName} not found for device switch", name);
                    return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
                }

                logger.LogInformation("API: Player {PlayerName} switched to device {Device}",
                    name, request.Device ?? "(default)");
                return Results.Ok(new SuccessResponse(true, $"Device switched to '{request.Device ?? "default"}'"));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning("API: Device switch bad request - {Message}", ex.Message);
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to switch device for player {PlayerName}", name);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to switch device");
            }
        })
        .WithName("SwitchDevice")
        .WithDescription("Hot-switch audio device without stopping playback");

        // PUT /api/players/{name}/volume - Set volume
        group.MapPut("/{name}/volume", async (
            string name,
            VolumeRequest request,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger,
            CancellationToken ct) =>
        {
            logger.LogDebug("API: PUT /api/players/{PlayerName}/volume to {Volume}", name, request.Volume);
            try
            {
                var success = await manager.SetVolumeAsync(name, request.Volume, ct);
                if (!success)
                {
                    logger.LogDebug("API: Player {PlayerName} not found for volume change", name);
                    return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
                }

                return Results.Ok(new SuccessResponse(true, $"Volume set to {request.Volume}"));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to set volume for player {PlayerName}", name);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to set volume");
            }
        })
        .WithName("SetVolume")
        .WithDescription("Set player volume (0-100)");

        // PUT /api/players/{name}/mute - Set mute state
        group.MapPut("/{name}/mute", (
            string name,
            MuteRequest request,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger) =>
        {
            logger.LogDebug("API: PUT /api/players/{PlayerName}/mute to {Muted}", name, request.Muted);
            var success = manager.SetMuted(name, request.Muted);
            if (!success)
            {
                logger.LogDebug("API: Player {PlayerName} not found for mute change", name);
                return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
            }

            return Results.Ok(new SuccessResponse(true, request.Muted ? "Muted" : "Unmuted"));
        })
        .WithName("SetMute")
        .WithDescription("Mute or unmute player");

        // PUT /api/players/{name}/offset - Set delay offset
        group.MapPut("/{name}/offset", (
            string name,
            OffsetRequest request,
            PlayerManagerService manager,
            ConfigurationService config,
            ILogger<PlayerManagerService> logger) =>
        {
            logger.LogDebug("API: PUT /api/players/{PlayerName}/offset to {DelayMs}ms", name, request.DelayMs);
            var player = manager.GetPlayer(name);
            if (player == null)
            {
                logger.LogDebug("API: Player {PlayerName} not found for offset change", name);
                return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
            }

            // Update config and save
            config.UpdatePlayerField(name, c => c.DelayMs = request.DelayMs);

            return Results.Ok(new SuccessResponse(true, $"Offset set to {request.DelayMs}ms"));
        })
        .WithName("SetOffset")
        .WithDescription("Set player delay offset in milliseconds");

        // POST /api/players/{name}/pause - Pause playback
        group.MapPost("/{name}/pause", (
            string name,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger) =>
        {
            logger.LogDebug("API: POST /api/players/{PlayerName}/pause", name);
            var player = manager.GetPlayer(name);
            if (player == null)
            {
                logger.LogDebug("API: Player {PlayerName} not found for pause", name);
                return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
            }

            manager.PausePlayer(name);
            return Results.Ok(new SuccessResponse(true, "Playback paused"));
        })
        .WithName("PausePlayer")
        .WithDescription("Pause player playback");

        // POST /api/players/{name}/resume - Resume playback
        group.MapPost("/{name}/resume", (
            string name,
            PlayerManagerService manager,
            ILogger<PlayerManagerService> logger) =>
        {
            logger.LogDebug("API: POST /api/players/{PlayerName}/resume", name);
            var player = manager.GetPlayer(name);
            if (player == null)
            {
                logger.LogDebug("API: Player {PlayerName} not found for resume", name);
                return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
            }

            manager.ResumePlayer(name);
            return Results.Ok(new SuccessResponse(true, "Playback resumed"));
        })
        .WithName("ResumePlayer")
        .WithDescription("Resume player playback");
    }
}

/// <summary>
/// Request to set mute state.
/// </summary>
public record MuteRequest(bool Muted);
