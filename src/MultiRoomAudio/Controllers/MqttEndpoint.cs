using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

public static class MqttEndpoint
{
    public static void MapMqttEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mqtt");

        group.MapGet("", (MqttConfigService config, MqttService mqtt) =>
            Results.Ok(ToResponse(config, mqtt)))
            .WithTags("MQTT").WithName("GetMqttSettings");

        group.MapPut("", (MqttSettingsUpdateRequest request, MqttConfigService config, MqttService mqtt) =>
        {
            config.Update(request);
            return Results.Ok(new
            {
                success = true,
                message = "MQTT settings saved. Restart the add-on/container to apply.",
                settings = ToResponse(config, mqtt)
            });
        })
            .WithTags("MQTT").WithName("UpdateMqttSettings");

        group.MapGet("/status", (MqttConfigService config, MqttService mqtt) =>
            Results.Ok(new { connected = mqtt.IsConnected, lastError = mqtt.LastError, source = config.Source }))
            .WithTags("MQTT").WithName("GetMqttStatus");
    }

    private static MqttSettingsResponse ToResponse(MqttConfigService config, MqttService mqtt)
    {
        var s = config.Current;
        return new MqttSettingsResponse(
            s.Enabled, s.Host, s.Port, s.Username, !string.IsNullOrEmpty(s.Password),
            s.UseTls, s.DiscoveryPrefix, s.BaseTopic, mqtt.IsConnected, mqtt.LastError, config.Source);
    }
}
