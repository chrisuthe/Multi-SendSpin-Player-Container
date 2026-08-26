using MultiRoomAudio.Audio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;
using MultiRoomAudio.Utilities;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for player management.
/// </summary>
public static class PlayersEndpoint
{
    #region Helper Methods

    /// <summary>
    /// Creates a standardized NotFound response for missing players.
    /// Centralizes error message formatting to avoid duplication.
    /// </summary>
    private static IResult PlayerNotFoundResult(string name, ILogger logger, string action)
    {
        logger.LogDebug("API: Player {PlayerName} not found for {Action}", name, action);
        return Results.NotFound(new ErrorResponse(false, $"Player '{name}' not found"));
    }

    #endregion

    /// <summary>
    /// Registers all player management API endpoints with the application.
    /// </summary>
    /// <remarks>
    /// Endpoints:
    /// <list type="bullet">
    /// <item>GET /api/players - List all players</item>
    /// <item>GET /api/players/{name} - Get specific player</item>
    /// <item>GET /api/players/{name}/stats - Get real-time audio diagnostics</item>
    /// <item>POST /api/players - Create new player</item>
    /// <item>PUT /api/players/{name} - Update player configuration</item>
    /// <item>DELETE /api/players/{name} - Delete player</item>
    /// <item>POST /api/players/{name}/stop - Stop player</item>
    /// <item>POST /api/players/{name}/start - Start stopped player</item>
    /// <item>POST /api/players/{name}/restart - Restart player</item>
    /// <item>PUT /api/players/{name}/volume - Set volume (0-100)</item>
    /// <item>PUT /api/players/{name}/startup-volume - Set startup volume</item>
    /// <item>PUT /api/players/{name}/mute - Set mute state</item>
    /// <item>PUT /api/players/{name}/auto-resume - Enable/disable auto-resume on device reconnect</item>
    /// <item>PUT /api/players/{name}/offset - Set output delay (0 to 5000ms)</item>
    /// <item>PUT /api/players/{name}/device - Switch audio device</item>
    /// <item>PUT /api/players/{name}/rename - Rename player</item>
    /// <item>POST /api/players/{name}/pause - Pause playback</item>
    /// <item>POST /api/players/{name}/resume - Resume playback</item>
    /// </list>
    /// </remarks>
    /// <param name="app">The WebApplication to register endpoints on.</param>
    public static void MapPlayersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/players")
            .WithTags("Players")
            .WithOpenApi();

        // GET /api/players/formats - Format options the selected device can actually do
        group.MapGet("/formats", (
            string? device,
            DeviceMatchingService deviceMatching,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: GET /api/players/formats?device={Device}", device ?? "(default)");

            // Same resolution the player itself uses, so the options offered here are the ones it
            // will advertise. Omitting the device means the default sink, not "no device".
            var capabilities = deviceMatching.GetFormatCapabilities(device);

            return Results.Ok(new AudioFormatsResponse(
                AudioFormatCatalog.BuildOptions(capabilities),
                AudioFormatCatalog.GetDefaultFormatId(capabilities)));
        })
        .WithName("GetAudioFormats")
        .WithDescription("Get the audio formats a device can be advertised as supporting");

        // GET /api/players - List all players
        group.MapGet("/", (PlayerManagerService manager, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: GET /api/players");
            var response = manager.GetAllPlayers();
            logger.LogDebug("API: Returning {PlayerCount} players", response.Count);
            return Results.Ok(response);
        })
        .WithName("ListPlayers")
        .WithDescription("Get all active players");

        // GET /api/players/{name} - Get specific player
        group.MapGet("/{name}", (string name, PlayerManagerService manager, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: GET /api/players/{PlayerName}", name);
            var player = manager.GetPlayer(name);
            if (player == null)
                return PlayerNotFoundResult(name, logger, "get");

            return Results.Ok(player);
        })
        .WithName("GetPlayer")
        .WithDescription("Get details of a specific player");

        // GET /api/players/{name}/stats - Get real-time player stats (Stats for Nerds)
        group.MapGet("/{name}/stats", (string name, PlayerManagerService manager, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: GET /api/players/{PlayerName}/stats", name);
            var stats = manager.GetPlayerStats(name);
            if (stats == null)
                return PlayerNotFoundResult(name, logger, "stats");

            return Results.Ok(stats);
        })
        .WithName("GetPlayerStats")
        .WithDescription("Get real-time audio diagnostics and sync metrics (Stats for Nerds)");

        // POST /api/players - Create new player
        group.MapPost("/", async (
            PlayerCreateRequest request,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: POST /api/players - Creating player {PlayerName}", request.Name);
            return await ApiExceptionHandler.ExecuteAsync(async () =>
            {
                var player = await manager.CreatePlayerAsync(request, ct);
                logger.LogInformation("API: Player {PlayerName} created successfully", player.Name);
                return Results.Created($"/api/players/{player.Name}", player);
            }, logger, "create player", request.Name);
        })
        .WithName("CreatePlayer")
        .WithDescription("Create and start a new player");

        // DELETE /api/players/{name} - Stop and remove player (and config)
        group.MapDelete("/{name}", async (
            string name,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: DELETE /api/players/{PlayerName}", name);
            var deleted = await manager.DeletePlayerAsync(name);
            if (!deleted)
                return PlayerNotFoundResult(name, logger, "deletion");

            logger.LogInformation("API: Player {PlayerName} deleted successfully", name);
            return Results.Ok(new SuccessResponse(true, $"Player '{name}' deleted"));
        })
        .WithName("DeletePlayer")
        .WithDescription("Stop and remove a player (also removes from config)");

        // POST /api/players/{name}/stop - Stop player (keeps config)
        group.MapPost("/{name}/stop", async (
            string name,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: POST /api/players/{PlayerName}/stop", name);
            var stopped = await manager.StopPlayerAsync(name);
            if (!stopped)
                return PlayerNotFoundResult(name, logger, "stop");

            logger.LogInformation("API: Player {PlayerName} stopped successfully", name);
            return Results.Ok(new SuccessResponse(true, $"Player '{name}' stopped"));
        })
        .WithName("StopPlayer")
        .WithDescription("Stop a player (config preserved for restart)");

        // POST /api/players/{name}/start - Start a stopped player
        group.MapPost("/{name}/start", async (
            string name,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: POST /api/players/{PlayerName}/start", name);
            return await ApiExceptionHandler.ExecuteAsync(async () =>
            {
                var player = await manager.StartPlayerAsync(name, ct);
                if (player == null)
                    return PlayerNotFoundResult(name, logger, "start");

                logger.LogInformation("API: Player {PlayerName} started successfully", name);
                return Results.Ok(player);
            }, logger, "start player", name);
        })
        .WithName("StartPlayer")
        .WithDescription("Start a stopped player");

        // POST /api/players/{name}/restart - Restart player
        group.MapPost("/{name}/restart", async (
            string name,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: POST /api/players/{PlayerName}/restart", name);
            return await ApiExceptionHandler.ExecuteAsync(async () =>
            {
                var player = await manager.RestartPlayerAsync(name, ct);
                if (player == null)
                    return PlayerNotFoundResult(name, logger, "restart");

                logger.LogInformation("API: Player {PlayerName} restarted successfully", name);
                return Results.Ok(player);
            }, logger, "restart player", name);
        })
        .WithName("RestartPlayer")
        .WithDescription("Stop and restart a player");

        // PUT /api/players/{name}/device - Switch audio device
        group.MapPut("/{name}/device", async (
            string name,
            DeviceSwitchRequest request,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: PUT /api/players/{PlayerName}/device to {Device}",
                name, request.Device ?? "(default)");
            return await ApiExceptionHandler.ExecuteAsync(async () =>
            {
                var success = await manager.SwitchDeviceAsync(name, request.Device, ct);
                if (!success)
                    return PlayerNotFoundResult(name, logger, "device switch");

                logger.LogInformation("API: Player {PlayerName} switched to device {Device}",
                    name, request.Device ?? "(default)");
                return Results.Ok(new SuccessResponse(true, $"Device switched to '{request.Device ?? "default"}'"));
            }, logger, "switch device", name);
        })
        .WithName("SwitchDevice")
        .WithDescription("Hot-switch audio device without stopping playback");

        // PUT /api/players/{name}/volume - Set volume
        group.MapPut("/{name}/volume", async (
            string name,
            VolumeRequest request,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogInformation("VOLUME [API] PUT /api/players/{Name}/volume: {Volume}%", name, request.Volume);
            return await ApiExceptionHandler.ExecuteAsync(async () =>
            {
                var success = await manager.SetVolumeAsync(name, request.Volume, ct);
                if (!success)
                    return PlayerNotFoundResult(name, logger, "volume change");

                return Results.Ok(new SuccessResponse(true, $"Volume set to {request.Volume}"));
            }, logger, "set volume", name);
        })
        .WithName("SetVolume")
        .WithDescription("Set player volume (0-100)");

        // PUT /api/players/{name}/startup-volume - Set startup volume
        group.MapPut("/{name}/startup-volume", (
            string name,
            VolumeRequest request,
            ConfigurationService config,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogInformation("VOLUME [API] PUT /api/players/{Name}/startup-volume: {Volume}%", name, request.Volume);
            return ApiExceptionHandler.Execute(() =>
            {
                // Update persisted config only
                if (!config.Players.TryGetValue(name, out var playerConfig))
                    return PlayerNotFoundResult(name, logger, "set startup volume");

                playerConfig.Volume = Math.Clamp(request.Volume, 0, 100);
                config.Save();

                logger.LogInformation("VOLUME [StartupConfig] Player '{Name}': startup volume set to {Volume}%",
                    name, request.Volume);

                return Results.Ok(new SuccessResponse(true,
                    $"Startup volume set to {request.Volume}% (takes effect on next restart)"));
            }, logger, "set startup volume", name);
        })
        .WithName("SetStartupVolume")
        .WithDescription("Set player startup volume (0-100) - takes effect on restart");

        // PUT /api/players/{name}/mute - Set mute state
        group.MapPut("/{name}/mute", (
            string name,
            MuteRequest request,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: PUT /api/players/{PlayerName}/mute to {Muted}", name, request.Muted);
            var success = manager.SetMuted(name, request.Muted);
            if (!success)
                return PlayerNotFoundResult(name, logger, "mute change");

            return Results.Ok(new SuccessResponse(true, request.Muted ? "Muted" : "Unmuted"));
        })
        .WithName("SetMute")
        .WithDescription("Mute or unmute player");

        // PUT /api/players/{name}/auto-resume - Enable/disable auto-resume
        group.MapPut("/{name}/auto-resume", (
            string name,
            AutoResumeRequest request,
            ConfigurationService config,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogInformation("API: PUT /api/players/{PlayerName}/auto-resume: {Enabled}", name, request.Enabled);
            return ApiExceptionHandler.Execute(() =>
            {
                if (!config.Players.TryGetValue(name, out var playerConfig))
                    return PlayerNotFoundResult(name, logger, "set auto-resume");

                playerConfig.AutoResume = request.Enabled;
                config.Save();

                logger.LogInformation("Player '{Name}': auto-resume {State}",
                    name, request.Enabled ? "enabled" : "disabled");

                return Results.Ok(new SuccessResponse(true,
                    $"Auto-resume {(request.Enabled ? "enabled" : "disabled")}"));
            }, logger, "set auto-resume", name);
        })
        .WithName("SetAutoResume")
        .WithDescription("Enable or disable auto-resume when audio device is reconnected");

        // PUT /api/players/{name}/offset - Set output delay
        group.MapPut("/{name}/offset", (
            string name,
            OffsetRequest request,
            PlayerManagerService manager,
            ConfigurationService config,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: PUT /api/players/{PlayerName}/offset to {DelayMs}ms", name, request.DelayMs);

            // Apply to running player (affects clock sync timing immediately)
            var applied = manager.SetDelayOffset(name, request.DelayMs);
            if (applied is not { } delayMs)
                return PlayerNotFoundResult(name, logger, "offset change");

            // Persist what actually took effect, not the raw request: persisting the request would
            // let an out-of-range value survive in config and load back on restart having never
            // been the running value.
            config.UpdatePlayerField(name, c => c.OutputDelayMs = delayMs);

            return Results.Ok(new SuccessResponse(true, $"Output delay set to {delayMs}ms"));
        })
        .AddEndpointFilter<ValidationFilter<OffsetRequest>>()
        .WithName("SetOffset")
        .WithDescription("Set player output delay in milliseconds (0-5000, per the Sendspin spec)");

        // POST /api/players/{name}/pause - Pause playback
        group.MapPost("/{name}/pause", (
            string name,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: POST /api/players/{PlayerName}/pause", name);
            return ApiExceptionHandler.Execute(() =>
            {
                var player = manager.GetPlayer(name);
                if (player == null)
                    return PlayerNotFoundResult(name, logger, "pause");

                manager.PausePlayer(name);
                return Results.Ok(new SuccessResponse(true, "Playback paused"));
            }, logger, "pause player", name);
        })
        .WithName("PausePlayer")
        .WithDescription("Pause player playback");

        // POST /api/players/{name}/resume - Resume playback
        group.MapPost("/{name}/resume", (
            string name,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: POST /api/players/{PlayerName}/resume", name);
            return ApiExceptionHandler.Execute(() =>
            {
                var player = manager.GetPlayer(name);
                if (player == null)
                    return PlayerNotFoundResult(name, logger, "resume");

                manager.ResumePlayer(name);
                return Results.Ok(new SuccessResponse(true, "Playback resumed"));
            }, logger, "resume player", name);
        })
        .WithName("ResumePlayer")
        .WithDescription("Resume player playback");

        // PUT /api/players/{name} - Update player configuration
        group.MapPut("/{name}", async (
            string name,
            PlayerUpdateRequest request,
            PlayerManagerService manager,
            ConfigurationService config,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: PUT /api/players/{PlayerName}", name);
            return await ApiExceptionHandler.ExecuteAsync(async () =>
            {
                // Check player exists
                var player = manager.GetPlayer(name);
                if (player == null)
                    return PlayerNotFoundResult(name, logger, "update");

                var needsRestart = false;
                var currentName = name;

                // Handle rename first (affects subsequent operations)
                // Note: a player's name only reaches Music Assistant via the Sendspin
                // client/hello on (re)connect, and MA keys players by client_id (derived
                // from the name). A rename therefore requires a restart to re-announce
                // under the new name-derived id; the UI auto-restarts to apply it.
                if (!string.IsNullOrEmpty(request.Name) && request.Name != name)
                {
                    var renamed = manager.RenamePlayer(name, request.Name);
                    if (!renamed)
                        return Results.Conflict(new ErrorResponse(false, $"Player '{request.Name}' already exists"));
                    currentName = request.Name;
                    needsRestart = true; // Name change requires restart for MA sync
                    logger.LogInformation("API: Player renamed from {OldName} to {NewName}", name, request.Name);
                }

                // Apply live changes - check return values to detect if player disappeared
                if (request.Volume.HasValue)
                {
                    var volumeSet = await manager.SetVolumeAsync(currentName, request.Volume.Value, ct);
                    if (!volumeSet)
                    {
                        logger.LogWarning("API: Player '{PlayerName}' disappeared during update (volume)", currentName);
                        return Results.Problem(
                            detail: $"Player '{currentName}' was removed during update",
                            statusCode: 409,
                            title: "Update conflict");
                    }
                }

                if (request.Device != null)
                {
                    var deviceSet = await manager.SwitchDeviceAsync(currentName, request.Device == "" ? null : request.Device, ct);
                    if (!deviceSet)
                    {
                        logger.LogWarning("API: Player '{PlayerName}' disappeared during update (device)", currentName);
                        return Results.Problem(
                            detail: $"Player '{currentName}' was removed during update",
                            statusCode: 409,
                            title: "Update conflict");
                    }
                }

                // Update config for changes that require restart
                var savedConfig = config.GetPlayer(currentName);
                if (savedConfig != null)
                {
                    // Persist volume as startup volume (takes effect on next restart)
                    if (request.Volume.HasValue)
                    {
                        savedConfig.Volume = Math.Clamp(request.Volume.Value, 0, 100);
                        logger.LogInformation("VOLUME [StartupConfig] Player '{Name}': startup volume set to {Volume}%",
                            currentName, savedConfig.Volume);
                    }

                    // Persist device change. supported_formats is derived from the device, and
                    // SwitchDeviceAsync only swaps the pipeline - the player has to restart to
                    // re-announce, or MA keeps offering the previous device's formats.
                    if (request.Device != null && request.Device != savedConfig.Device)
                    {
                        savedConfig.Device = request.Device;
                        needsRestart = true;
                        logger.LogInformation("API: Player {PlayerName} device persisted to '{Device}'",
                            currentName, savedConfig.Device == "" ? "(none)" : savedConfig.Device);
                    }

                    if (request.ServerUrl != null && request.ServerUrl != (savedConfig.Server ?? ""))
                    {
                        savedConfig.Server = request.ServerUrl == "" ? null : request.ServerUrl;
                        needsRestart = true;
                    }

                    // Handle advertised format change
                    if (request.AdvertisedFormat != null &&
                        request.AdvertisedFormat != savedConfig.AdvertisedFormat)
                    {
                        savedConfig.AdvertisedFormat = request.AdvertisedFormat == "" ? null : request.AdvertisedFormat;
                        needsRestart = true;
                        logger.LogInformation("API: Player {PlayerName} advertised format changed to '{Format}'",
                            currentName, savedConfig.AdvertisedFormat ?? "(device default)");
                    }

                    // Handle buffer size change
                    if (request.BufferSizeMs.HasValue && request.BufferSizeMs.Value != savedConfig.BufferSizeMs)
                    {
                        savedConfig.BufferSizeMs = request.BufferSizeMs.Value;
                        needsRestart = true;
                        logger.LogInformation("API: Player {PlayerName} buffer size changed to {BufferSizeMs}ms",
                            currentName, savedConfig.BufferSizeMs);
                    }

                    config.Save();
                }

                // Return response indicating if restart is needed
                return Results.Ok(new
                {
                    success = true,
                    message = needsRestart
                        ? "Configuration updated. Restart required for changes to take effect."
                        : "Player updated successfully.",
                    needsRestart,
                    playerName = currentName
                });
            }, logger, "update player", name);
        })
        .WithName("UpdatePlayer")
        .WithDescription("Update player configuration. Returns whether restart is needed for changes to take effect.");

        // PUT /api/players/{name}/rename - Rename a player
        group.MapPut("/{name}/rename", (
            string name,
            RenameRequest request,
            PlayerManagerService manager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PlayersEndpoint");
            logger.LogDebug("API: PUT /api/players/{PlayerName}/rename to {NewName}", name, request.NewName);
            return ApiExceptionHandler.Execute(() =>
            {
                var success = manager.RenamePlayer(name, request.NewName);
                if (!success)
                    return PlayerNotFoundResult(name, logger, "rename");

                logger.LogInformation("API: Player {PlayerName} renamed to {NewName}", name, request.NewName);

                // Return response with restart hint - the name change is saved locally
                // but the SDK's ClientName is set at creation time, so Music Assistant
                // will still see the old name until the player is restarted
                return Results.Ok(new PlayerRenameResponse(
                    Success: true,
                    Message: $"Player renamed to '{request.NewName}'",
                    NewName: request.NewName,
                    RestartRequired: true,
                    RestartHint: "Restart the player for the name change to appear in Music Assistant."
                ));
            }, logger, "rename player", name);
        })
        .WithName("RenamePlayer")
        .WithDescription("Rename a player to a new name. The new name reaches Music Assistant only after a restart, which re-announces the player under its new name-derived client id.");
    }
}
