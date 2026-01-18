using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for 12V trigger relay control.
/// </summary>
public static class TriggersEndpoint
{
    public static void MapTriggersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/triggers")
            .WithTags("Triggers")
            .WithOpenApi();

        // GET /api/triggers - Get trigger feature status and all channels
        group.MapGet("/", (TriggerService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers");
            var response = service.GetStatus();
            return Results.Ok(response);
        })
        .WithName("GetTriggerStatus")
        .WithDescription("Get the status of the 12V trigger feature and all 8 channels");

        // PUT /api/triggers/enabled - Enable or disable the trigger feature
        group.MapPut("/enabled", (
            TriggerFeatureEnableRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: PUT /api/triggers/enabled - {Enabled}, ChannelCount={ChannelCount}",
                request.Enabled, request.ChannelCount);

            var success = service.SetEnabled(request.Enabled, request.FtdiSerialNumber, request.ChannelCount);
            var status = service.GetStatus();

            if (success)
            {
                logger.LogInformation("Trigger feature {Action}", request.Enabled ? "enabled" : "disabled");
                return Results.Ok(status);
            }
            else
            {
                logger.LogWarning("Failed to {Action} trigger feature: {Error}",
                    request.Enabled ? "enable" : "disable", status.ErrorMessage);
                return Results.Ok(status); // Return status with error message
            }
        })
        .WithName("SetTriggerEnabled")
        .WithDescription("Enable or disable the 12V trigger feature");

        // PUT /api/triggers/channels - Update channel count
        group.MapPut("/channels", (
            ChannelCountRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: PUT /api/triggers/channels - {ChannelCount}", request.ChannelCount);

            if (!ValidChannelCounts.IsValid(request.ChannelCount))
            {
                return Results.BadRequest(new ErrorResponse(false,
                    $"Channel count must be one of: {string.Join(", ", ValidChannelCounts.Values)}"));
            }

            var success = service.SetChannelCount(request.ChannelCount);
            var status = service.GetStatus();

            if (success)
            {
                logger.LogInformation("Channel count updated to {Count}", request.ChannelCount);
                return Results.Ok(status);
            }
            else
            {
                return Results.BadRequest(new ErrorResponse(false, "Failed to update channel count"));
            }
        })
        .WithName("SetChannelCount")
        .WithDescription("Update the number of relay channels (1, 2, 4, 8, or 16)");

        // GET /api/triggers/devices - List available FTDI devices
        group.MapGet("/devices", (TriggerService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers/devices");

            var devices = service.GetAvailableDevices();
            return Results.Ok(new
            {
                devices,
                count = devices.Count,
                libraryAvailable = devices.Count > 0 || Relay.FtdiRelayBoard.IsLibraryAvailable()
            });
        })
        .WithName("ListFtdiDevices")
        .WithDescription("List available FTDI devices for relay board connection");

        // GET /api/triggers/{channel} - Get single channel status
        group.MapGet("/{channel:int}", (
            int channel,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers/{Channel}", channel);

            if (channel < 1 || channel > 8)
            {
                return Results.BadRequest(new ErrorResponse(false, "Channel must be between 1 and 8"));
            }

            var status = service.GetStatus();
            var trigger = status.Triggers.FirstOrDefault(t => t.Channel == channel);

            return trigger != null
                ? Results.Ok(trigger)
                : Results.NotFound(new ErrorResponse(false, $"Channel {channel} not found"));
        })
        .WithName("GetTriggerChannel")
        .WithDescription("Get status of a specific trigger channel");

        // PUT /api/triggers/{channel} - Configure a trigger channel
        group.MapPut("/{channel:int}", (
            int channel,
            TriggerConfigureRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: PUT /api/triggers/{Channel}", channel);

            if (channel < 1 || channel > 8)
            {
                return Results.BadRequest(new ErrorResponse(false, "Channel must be between 1 and 8"));
            }

            // Validate offDelaySeconds is not negative
            if (request.OffDelaySeconds < 0)
            {
                return Results.BadRequest(new ErrorResponse(false, "Off delay must be 0 or greater"));
            }

            // Validate offDelaySeconds doesn't exceed maximum (1 hour)
            if (request.OffDelaySeconds > 3600)
            {
                return Results.BadRequest(new ErrorResponse(false, "Off delay must not exceed 3600 seconds (1 hour)"));
            }

            // Override channel from route
            request.Channel = channel;

            try
            {
                var success = service.ConfigureTrigger(
                    channel,
                    request.CustomSinkName,
                    request.OffDelaySeconds,
                    request.ZoneName);

                var status = service.GetStatus();
                var trigger = status.Triggers.FirstOrDefault(t => t.Channel == channel);

                return Results.Ok(trigger);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
        })
        .WithName("ConfigureTrigger")
        .WithDescription("Configure a trigger channel mapping");

        // DELETE /api/triggers/{channel} - Unassign a trigger channel
        group.MapDelete("/{channel:int}", (
            int channel,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: DELETE /api/triggers/{Channel}", channel);

            if (channel < 1 || channel > 8)
            {
                return Results.BadRequest(new ErrorResponse(false, "Channel must be between 1 and 8"));
            }

            service.ConfigureTrigger(channel, null, 60, null);
            logger.LogInformation("Trigger channel {Channel} unassigned", channel);

            return Results.Ok(new SuccessResponse(true, $"Trigger channel {channel} unassigned"));
        })
        .WithName("UnassignTrigger")
        .WithDescription("Unassign a trigger channel");

        // POST /api/triggers/{channel}/test - Manual relay test
        group.MapPost("/{channel:int}/test", (
            int channel,
            RelayManualControlRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: POST /api/triggers/{Channel}/test - {On}", channel, request.On);

            if (channel < 1 || channel > 8)
            {
                return Results.BadRequest(new ErrorResponse(false, "Channel must be between 1 and 8"));
            }

            var status = service.GetStatus();
            if (status.State != TriggerFeatureState.Connected)
            {
                return Results.BadRequest(new ErrorResponse(false,
                    $"Relay board not connected (state: {status.State})"));
            }

            var success = service.ManualControl(channel, request.On);
            if (success)
            {
                return Results.Ok(new SuccessResponse(true,
                    $"Relay {channel} set to {(request.On ? "ON" : "OFF")}"));
            }
            else
            {
                return Results.Problem(
                    detail: "Failed to control relay",
                    statusCode: 500,
                    title: "Relay control failed");
            }
        })
        .WithName("TestRelay")
        .WithDescription("Manually control a relay for testing");

        // POST /api/triggers/reconnect - Try to reconnect to the relay board
        group.MapPost("/reconnect", (
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: POST /api/triggers/reconnect");

            var status = service.GetStatus();
            if (!status.Enabled)
            {
                return Results.BadRequest(new ErrorResponse(false, "Trigger feature is not enabled"));
            }

            // Re-enable to trigger reconnection
            service.SetEnabled(true, status.FtdiSerialNumber);
            status = service.GetStatus();

            return Results.Ok(status);
        })
        .WithName("ReconnectRelayBoard")
        .WithDescription("Attempt to reconnect to the relay board");
    }
}
