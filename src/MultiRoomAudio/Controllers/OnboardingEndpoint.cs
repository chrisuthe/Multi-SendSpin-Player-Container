using System.ComponentModel.DataAnnotations;
using MultiRoomAudio.Audio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;
using MultiRoomAudio.Utilities;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for the onboarding wizard.
/// </summary>
public static class OnboardingEndpoint
{
    /// <summary>
    /// Registers onboarding wizard API endpoints with the application.
    /// </summary>
    /// <remarks>
    /// Endpoints:
    /// <list type="bullet">
    /// <item>GET /api/onboarding/status - Get onboarding wizard status</item>
    /// <item>POST /api/onboarding/complete - Mark onboarding as completed</item>
    /// <item>POST /api/onboarding/skip - Skip the onboarding wizard</item>
    /// <item>POST /api/onboarding/reset - Reset onboarding to allow re-running</item>
    /// <item>POST /api/onboarding/create-players - Batch create players</item>
    /// <item>POST /api/devices/{id}/test-tone - Play test tone through device</item>
    /// </list>
    /// </remarks>
    /// <param name="app">The WebApplication to register endpoints on.</param>
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
            return await ApiExceptionHandler.ExecuteAsync(async () =>
            {
                // Find the device
                var device = backendFactory.GetDevice(id);
                if (device == null)
                    return Results.NotFound(new ErrorResponse(false, $"Device '{id}' not found"));

                // For multi-channel devices with a specific channel requested,
                // use PlayChannelToneAsync with --channel-map for direct routing
                if (!string.IsNullOrEmpty(request?.ChannelName) && device.MaxChannels > 2)
                {
                    // Validate channel exists on device if we have channel map info
                    if (device.ChannelMap != null &&
                        !device.ChannelMap.Contains(request.ChannelName, StringComparer.OrdinalIgnoreCase))
                    {
                        return Results.BadRequest(new ErrorResponse(false,
                            $"Channel '{request.ChannelName}' not found on device. " +
                            $"Available channels: {string.Join(", ", device.ChannelMap)}"));
                    }

                    logger.LogInformation(
                        "Multi-channel test: Playing to device '{Device}' channel {ChannelName} via --channel-map",
                        device.Id, request.ChannelName);

                    await toneGenerator.PlayChannelToneAsync(
                        device.Id,
                        request.ChannelName,
                        request.FrequencyHz ?? 1000,
                        request.DurationMs ?? 1500,
                        ct);

                    return Results.Ok(new
                    {
                        success = true,
                        message = "Test tone played successfully",
                        deviceId = id,
                        deviceName = device.Name,
                        frequencyHz = request.FrequencyHz ?? 1000,
                        durationMs = request.DurationMs ?? 1500,
                        channelName = request.ChannelName
                    });
                }

                // Fallback: stereo devices or whole-device tests use original 2-channel method
                await toneGenerator.PlayTestToneAsync(
                    device.Id,
                    frequencyHz: request?.FrequencyHz ?? 1000,
                    durationMs: request?.DurationMs ?? 1500,
                    channelName: request?.ChannelName,
                    ct: ct);

                return Results.Ok(new
                {
                    success = true,
                    message = "Test tone played successfully",
                    deviceId = id,
                    deviceName = device.Name,
                    frequencyHz = request?.FrequencyHz ?? 1000,
                    durationMs = request?.DurationMs ?? 1500,
                    channelName = request?.ChannelName
                });
            }, logger, "play test tone", id);
        })
        .WithTags("Devices", "Onboarding")
        .WithName("PlayTestTone")
        .WithDescription("Play a test tone through a specific audio device for identification")
        .WithOpenApi();

        // POST /api/onboarding/create-players - Batch create players
        app.MapPost("/api/onboarding/create-players", async (
            BatchCreatePlayersRequest request,
            PlayerManagerService playerManager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("OnboardingEndpoint");
            logger.LogDebug("API: POST /api/onboarding/create-players with {Count} players", request.Players?.Count ?? 0);

            if (request.Players == null || request.Players.Count == 0)
            {
                return Results.BadRequest(new ErrorResponse(false, "No players specified"));
            }

            var result = await playerManager.BatchCreatePlayersAsync(request.Players, ct);

            // Transform failed list to match existing API contract (anonymous objects with name/error)
            var failed = result.Failed.Select(f => new { name = f.Name, error = f.Error }).ToList();

            return Results.Ok(new
            {
                success = result.Success,
                message = $"Created {result.CreatedCount} of {request.Players.Count} players, started {result.StartedCount}",
                created = result.Created,
                started = result.Started,
                failed,
                createdCount = result.CreatedCount,
                startedCount = result.StartedCount,
                failedCount = result.FailedCount
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
/// <param name="FrequencyHz">Tone frequency in Hz (20-20000). Default: 1000Hz.</param>
/// <param name="DurationMs">Tone duration in milliseconds (100-10000). Default: 1500ms.</param>
/// <param name="ChannelName">Optional channel name for multi-channel devices.</param>
public record TestToneRequest(
    [property: Range(20, 20000, ErrorMessage = "FrequencyHz must be between 20 and 20000.")]
    int? FrequencyHz = null,
    [property: Range(100, 10000, ErrorMessage = "DurationMs must be between 100 and 10000.")]
    int? DurationMs = null,
    string? ChannelName = null);
