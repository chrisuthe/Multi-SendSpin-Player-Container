using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

public static class SettingsEndpoint
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings");

        group.MapGet("/buffer", (EnvironmentService env, PlayerManagerService players) =>
        {
            var bufferSeconds = env.BufferSeconds;
            var playerCount = players.GetAllPlayers().Players.Count;

            return Results.Ok(new
            {
                bufferSeconds,
                playerCount,
                memoryEstimates = new[]
                {
                    new { sampleRate = 48000, label = "48 kHz", perPlayerMb = CalcMb(bufferSeconds, 48000), totalMb = CalcMb(bufferSeconds, 48000) * playerCount },
                    new { sampleRate = 96000, label = "96 kHz", perPlayerMb = CalcMb(bufferSeconds, 96000), totalMb = CalcMb(bufferSeconds, 96000) * playerCount },
                    new { sampleRate = 192000, label = "192 kHz", perPlayerMb = CalcMb(bufferSeconds, 192000), totalMb = CalcMb(bufferSeconds, 192000) * playerCount }
                }
            });
        })
        .WithTags("Settings")
        .WithName("GetBufferSettings");

        group.MapPut("/buffer", (BufferUpdateRequest request,
            EnvironmentService env,
            ConfigurationService config,
            PlayerManagerService players) =>
        {
            if (request.BufferSeconds < 5 || request.BufferSeconds > 30 || request.BufferSeconds % 5 != 0)
            {
                return Results.BadRequest(new { success = false, message = "Buffer size must be 5, 10, 15, 20, 25, or 30 seconds." });
            }

            env.BufferSeconds = request.BufferSeconds;
            config.SaveSettings(env.SettingsConfigPath, new GlobalSettings { BufferSeconds = request.BufferSeconds });

            var playerCount = players.GetAllPlayers().Players.Count;

            return Results.Ok(new
            {
                success = true,
                message = "Buffer size updated. Restart players to apply the new buffer size.",
                bufferSeconds = env.BufferSeconds,
                playerCount,
                memoryEstimates = new[]
                {
                    new { sampleRate = 48000, label = "48 kHz", perPlayerMb = CalcMb(env.BufferSeconds, 48000), totalMb = CalcMb(env.BufferSeconds, 48000) * playerCount },
                    new { sampleRate = 96000, label = "96 kHz", perPlayerMb = CalcMb(env.BufferSeconds, 96000), totalMb = CalcMb(env.BufferSeconds, 96000) * playerCount },
                    new { sampleRate = 192000, label = "192 kHz", perPlayerMb = CalcMb(env.BufferSeconds, 192000), totalMb = CalcMb(env.BufferSeconds, 192000) * playerCount }
                }
            });
        })
        .WithTags("Settings")
        .WithName("UpdateBufferSettings");
    }

    private static double CalcMb(int bufferSeconds, int sampleRate)
    {
        // buffer_seconds * sample_rate * 2 channels * 4 bytes (float32) / 1,048,576
        return Math.Round((double)bufferSeconds * sampleRate * 2 * 4 / 1_048_576, 1);
    }
}

public record BufferUpdateRequest(int BufferSeconds);
