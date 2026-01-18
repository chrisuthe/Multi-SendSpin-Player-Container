using MultiRoomAudio.Audio.PulseAudio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for health checks and service status.
/// </summary>
public static class HealthEndpoint
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // GET /api/health - Basic health check
        app.MapGet("/api/health", () =>
        {
            return Results.Ok(new HealthResponse(
                Status: "healthy",
                Timestamp: DateTime.UtcNow,
                Version: GetVersion()
            ));
        })
        .WithTags("Health")
        .WithName("HealthCheck")
        .WithDescription("Basic health check endpoint")
        .WithOpenApi();

        // GET /api/health/ready - Readiness check
        app.MapGet("/api/health/ready", (PlayerManagerService manager) =>
        {
            try
            {
                // Check if we can enumerate devices
                var devices = PulseAudioDeviceEnumerator.GetOutputDevices().ToList();
                var players = manager.GetAllPlayers();

                return Results.Ok(new
                {
                    status = "ready",
                    timestamp = DateTime.UtcNow,
                    checks = new
                    {
                        pulseaudio = devices.Count > 0 ? "ok" : "no_devices",
                        deviceCount = devices.Count,
                        playerCount = players.Count
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    status = "not_ready",
                    timestamp = DateTime.UtcNow,
                    error = ex.Message
                }, statusCode: 503);
            }
        })
        .WithTags("Health")
        .WithName("ReadinessCheck")
        .WithDescription("Readiness check for container orchestration")
        .WithOpenApi();

        // GET /api/health/live - Liveness check
        app.MapGet("/api/health/live", () =>
        {
            return Results.Ok(new
            {
                status = "alive",
                timestamp = DateTime.UtcNow
            });
        })
        .WithTags("Health")
        .WithName("LivenessCheck")
        .WithDescription("Liveness check for container orchestration")
        .WithOpenApi();

        // GET /api/status - Detailed service status
        app.MapGet("/api/status", (PlayerManagerService manager) =>
        {
            try
            {
                var devices = PulseAudioDeviceEnumerator.GetOutputDevices().ToList();
                var players = manager.GetAllPlayers();

                return Results.Ok(new
                {
                    service = "sendspin-service",
                    version = GetVersion(),
                    uptime = GetUptime(),
                    timestamp = DateTime.UtcNow,
                    players = new
                    {
                        total = players.Count,
                        playing = players.Players.Count(p => p.State == PlayerState.Playing),
                        connected = players.Players.Count(p => p.State == PlayerState.Connected),
                        errors = players.Players.Count(p => p.State == PlayerState.Error)
                    },
                    audio = new
                    {
                        deviceCount = devices.Count,
                        defaultDevice = devices.FirstOrDefault(d => d.IsDefault)?.Name
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to get service status");
            }
        })
        .WithTags("Status")
        .WithName("ServiceStatus")
        .WithDescription("Detailed service status including player and device counts")
        .WithOpenApi();
    }

    private static string GetVersion()
    {
        // First check environment variable set by Docker build args
        var envVersion = Environment.GetEnvironmentVariable("APP_VERSION");
        if (!string.IsNullOrEmpty(envVersion) && envVersion != "dev")
        {
            return envVersion;
        }

        // Fall back to assembly version
        var assembly = typeof(HealthEndpoint).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString(3) ?? "dev";
    }

    private static string GetUptime()
    {
        var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return uptime.ToString(@"d\.hh\:mm\:ss");
    }
}
