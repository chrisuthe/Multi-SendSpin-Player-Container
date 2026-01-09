using MultiRoomAudio.Audio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for the onboarding wizard.
/// </summary>
public static class OnboardingEndpoint
{
    public static void MapOnboardingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/onboarding")
            .WithTags("Onboarding")
            .WithOpenApi();

        // GET /api/onboarding/status - Get onboarding status
        group.MapGet("/status", (OnboardingService onboarding) =>
        {
            return Results.Ok(onboarding.GetStatus());
        })
        .WithName("GetOnboardingStatus")
        .WithDescription("Get the current onboarding wizard status");

        // POST /api/onboarding/complete - Mark onboarding as completed
        group.MapPost("/complete", (
            OnboardingCompleteRequest? request,
            OnboardingService onboarding,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("OnboardingEndpoint");
            logger.LogDebug("API: POST /api/onboarding/complete");

            onboarding.MarkCompleted(
                devicesConfigured: request?.DevicesConfigured ?? 0,
                playersCreated: request?.PlayersCreated ?? 0);

            return Results.Ok(new
            {
                success = true,
                message = "Onboarding completed",
                status = onboarding.GetStatus()
            });
        })
        .WithName("CompleteOnboarding")
        .WithDescription("Mark onboarding wizard as completed");

        // POST /api/onboarding/skip - Skip onboarding
        group.MapPost("/skip", (OnboardingService onboarding, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("OnboardingEndpoint");
            logger.LogDebug("API: POST /api/onboarding/skip");

            onboarding.Skip();

            return Results.Ok(new
            {
                success = true,
                message = "Onboarding skipped",
                status = onboarding.GetStatus()
            });
        })
        .WithName("SkipOnboarding")
        .WithDescription("Skip the onboarding wizard");

        // POST /api/onboarding/reset - Reset onboarding to allow re-running
        group.MapPost("/reset", (OnboardingService onboarding, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("OnboardingEndpoint");
            logger.LogDebug("API: POST /api/onboarding/reset");

            onboarding.Reset();

            return Results.Ok(new
            {
                success = true,
                message = "Onboarding reset. Wizard will show on next page load.",
                status = onboarding.GetStatus()
            });
        })
        .WithName("ResetOnboarding")
        .WithDescription("Reset onboarding state to allow re-running the wizard");

        // POST /api/devices/{id}/test-tone - Play test tone through device
        // Note: This is also mapped under /api/devices but included here for discoverability
        app.MapPost("/api/devices/{id}/test-tone", async (
            string id,
            TestToneRequest? request,
            BackendFactory backendFactory,
            ToneGeneratorService toneGenerator,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("OnboardingEndpoint");
            logger.LogDebug("API: POST /api/devices/{DeviceId}/test-tone", id);

            try
            {
                // Find the device
                var device = backendFactory.GetDevice(id);
                if (device == null)
                {
                    return Results.NotFound(new ErrorResponse(false, $"Device '{id}' not found"));
                }

                // Play test tone
                await toneGenerator.PlayTestToneAsync(
                    device.Id,
                    frequencyHz: request?.FrequencyHz ?? 1000,
                    durationMs: request?.DurationMs ?? 1500,
                    ct: ct);

                return Results.Ok(new
                {
                    success = true,
                    message = "Test tone played successfully",
                    deviceId = id,
                    deviceName = device.Name,
                    frequencyHz = request?.FrequencyHz ?? 1000,
                    durationMs = request?.DurationMs ?? 1500
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already playing"))
            {
                return Results.Conflict(new ErrorResponse(false, ex.Message));
            }
            catch (TimeoutException ex)
            {
                logger.LogWarning(ex, "Test tone playback timed out for device {DeviceId}", id);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 504,
                    title: "Test tone playback timed out");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to play test tone on device {DeviceId}", id);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to play test tone");
            }
        })
        .WithTags("Devices", "Onboarding")
        .WithName("PlayTestTone")
        .WithDescription("Play a test tone through a specific audio device for identification")
        .WithOpenApi();

        // POST /api/onboarding/create-players - Batch create players
        app.MapPost("/api/onboarding/create-players", async (
            BatchCreatePlayersRequest request,
            PlayerManagerService playerManager,
            ConfigurationService config,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("OnboardingEndpoint");
            logger.LogDebug("API: POST /api/onboarding/create-players with {Count} players", request.Players?.Count ?? 0);

            if (request.Players == null || request.Players.Count == 0)
            {
                return Results.BadRequest(new ErrorResponse(false, "No players specified"));
            }

            var created = new List<string>();
            var failed = new List<object>();

            foreach (var playerReq in request.Players)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(playerReq.Name))
                    {
                        failed.Add(new { name = playerReq.Name ?? "(empty)", error = "Player name is required" });
                        continue;
                    }

                    if (config.PlayerExists(playerReq.Name))
                    {
                        failed.Add(new { name = playerReq.Name, error = "Player already exists" });
                        continue;
                    }

                    // Create player configuration
                    var playerConfig = new PlayerConfiguration
                    {
                        Name = playerReq.Name,
                        Device = playerReq.Device ?? string.Empty,
                        Volume = playerReq.Volume ?? 75,
                        Autostart = playerReq.Autostart ?? true,
                        Provider = "sendspin"
                    };

                    config.SetPlayer(playerReq.Name, playerConfig);
                    created.Add(playerReq.Name);

                    logger.LogInformation("Created player from onboarding: {PlayerName}", playerReq.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create player {PlayerName}", playerReq.Name);
                    failed.Add(new { name = playerReq.Name, error = ex.Message });
                }
            }

            // Save all at once
            if (created.Count > 0)
            {
                config.Save();
            }

            return Results.Ok(new
            {
                success = failed.Count == 0,
                message = $"Created {created.Count} of {request.Players.Count} players",
                created,
                failed,
                createdCount = created.Count,
                failedCount = failed.Count
            });
        })
        .WithTags("Onboarding")
        .WithName("BatchCreatePlayers")
        .WithDescription("Create multiple players at once during onboarding")
        .WithOpenApi();
    }
}

/// <summary>
/// Request to complete onboarding.
/// </summary>
public record OnboardingCompleteRequest(int DevicesConfigured = 0, int PlayersCreated = 0);

/// <summary>
/// Request to play a test tone.
/// </summary>
public record TestToneRequest(int? FrequencyHz = null, int? DurationMs = null);

/// <summary>
/// Request for batch player creation.
/// </summary>
public record BatchCreatePlayersRequest(List<BatchPlayerRequest>? Players);

/// <summary>
/// Single player creation request.
/// </summary>
public record BatchPlayerRequest(
    string Name,
    string? Device = null,
    int? Volume = null,
    bool? Autostart = null);
