using System.Web;
using Microsoft.AspNetCore.Mvc;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for 12V trigger relay control.
/// Supports multiple relay boards.
/// </summary>
public static class TriggersEndpoint
{
    public static void MapTriggersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/triggers")
            .WithTags("Triggers")
            .WithOpenApi();

        // GET /api/triggers - Get trigger feature status (all boards)
        group.MapGet("/", (TriggerService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers");
            var response = service.GetStatus();
            return Results.Ok(response);
        })
        .WithName("GetTriggerStatus")
        .WithDescription("Get the status of the 12V trigger feature and all boards");

        // PUT /api/triggers/enabled - Enable or disable the trigger feature (all boards)
        group.MapPut("/enabled", (
            TriggerFeatureEnableRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: PUT /api/triggers/enabled - {Enabled}", request.Enabled);

            var success = service.SetEnabled(request.Enabled);
            var status = service.GetStatus();

            if (success)
            {
                logger.LogInformation("Trigger feature {Action}", request.Enabled ? "enabled" : "disabled");
                return Results.Ok(status);
            }
            else
            {
                logger.LogWarning("Failed to {Action} trigger feature", request.Enabled ? "enable" : "disable");
                return Results.Ok(status);
            }
        })
        .WithName("SetTriggerEnabled")
        .WithDescription("Enable or disable the 12V trigger feature for all boards");

        // GET /api/triggers/devices - List available FTDI devices (legacy)
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
        .WithDescription("List available FTDI devices for relay board connection (legacy - use /devices/all for both FTDI and HID)");

        // GET /api/triggers/devices/all - List all available relay devices (FTDI, HID, and Modbus)
        group.MapGet("/devices/all", (TriggerService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers/devices/all");

            var devices = service.GetAllAvailableDevices();
            return Results.Ok(new
            {
                devices,
                count = devices.Count,
                ftdiCount = devices.Count(d => d.BoardType == RelayBoardType.Ftdi),
                hidCount = devices.Count(d => d.BoardType == RelayBoardType.UsbHid),
                modbusCount = devices.Count(d => d.BoardType == RelayBoardType.Modbus)
            });
        })
        .WithName("ListAllRelayDevices")
        .WithDescription("List all available relay devices (FTDI, USB HID, and Modbus)");

        // ============================================
        // Board Management Endpoints
        // ============================================

        // GET /api/triggers/boards - List all configured boards
        group.MapGet("/boards", (TriggerService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers/boards");

            var status = service.GetStatus();
            return Results.Ok(new
            {
                boards = status.Boards,
                count = status.Boards.Count
            });
        })
        .WithName("ListBoards")
        .WithDescription("List all configured relay boards");

        // POST /api/triggers/boards - Add a new board
        group.MapPost("/boards", (
            AddBoardRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: POST /api/triggers/boards - {BoardId}", request.BoardId);

            if (string.IsNullOrWhiteSpace(request.BoardId))
            {
                return Results.BadRequest(new ErrorResponse(false, "Board ID is required"));
            }

            if (!ValidChannelCounts.IsValid(request.ChannelCount))
            {
                return Results.BadRequest(new ErrorResponse(false,
                    $"Channel count must be one of: {string.Join(", ", ValidChannelCounts.Values)}"));
            }

            var success = service.AddBoard(request.BoardId, request.DisplayName, request.ChannelCount, request.BoardType);
            if (success)
            {
                var boardStatus = service.GetBoardStatus(request.BoardId);
                logger.LogInformation("Added board '{BoardId}'", request.BoardId);
                return Results.Created($"/api/triggers/boards/{request.BoardId}", boardStatus);
            }
            else
            {
                return Results.Conflict(new ErrorResponse(false, $"Board '{request.BoardId}' already exists"));
            }
        })
        .WithName("AddBoard")
        .WithDescription("Add a new relay board");

        // GET /api/triggers/boards/{boardId} - Get specific board status
        group.MapGet("/boards/{boardId}", (
            string boardId,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers/boards/{BoardId}", boardId);

            var boardStatus = service.GetBoardStatus(boardId);
            if (boardStatus == null)
            {
                return Results.NotFound(new ErrorResponse(false, $"Board '{boardId}' not found"));
            }

            return Results.Ok(boardStatus);
        })
        .WithName("GetBoardStatus")
        .WithDescription("Get status of a specific relay board");

        // PUT /api/triggers/boards/{boardId} - Update board settings
        group.MapPut("/boards/{boardId}", (
            string boardId,
            UpdateBoardRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: PUT /api/triggers/boards/{BoardId}", boardId);

            if (request.ChannelCount.HasValue && !ValidChannelCounts.IsValid(request.ChannelCount.Value))
            {
                return Results.BadRequest(new ErrorResponse(false,
                    $"Channel count must be one of: {string.Join(", ", ValidChannelCounts.Values)}"));
            }

            var success = service.UpdateBoard(boardId, request.DisplayName, request.ChannelCount, request.StartupBehavior, request.ShutdownBehavior);
            if (success)
            {
                var boardStatus = service.GetBoardStatus(boardId);
                return Results.Ok(boardStatus);
            }
            else
            {
                return Results.NotFound(new ErrorResponse(false, $"Board '{boardId}' not found"));
            }
        })
        .WithName("UpdateBoard")
        .WithDescription("Update a relay board's settings (including startup behavior)");

        // DELETE /api/triggers/boards/{boardId} - Remove a board
        group.MapDelete("/boards/{boardId}", (
            string boardId,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: DELETE /api/triggers/boards/{BoardId}", boardId);

            var success = service.RemoveBoard(boardId);
            if (success)
            {
                logger.LogInformation("Removed board '{BoardId}'", boardId);
                return Results.Ok(new SuccessResponse(true, $"Board '{boardId}' removed"));
            }
            else
            {
                return Results.NotFound(new ErrorResponse(false, $"Board '{boardId}' not found"));
            }
        })
        .WithName("RemoveBoard")
        .WithDescription("Remove a relay board");

        // POST /api/triggers/boards/{boardId}/reconnect - Reconnect a specific board
        group.MapPost("/boards/{boardId}/reconnect", (
            string boardId,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: POST /api/triggers/boards/{BoardId}/reconnect", boardId);

            var success = service.ReconnectBoard(boardId);
            var boardStatus = service.GetBoardStatus(boardId);

            if (boardStatus == null)
            {
                return Results.NotFound(new ErrorResponse(false, $"Board '{boardId}' not found"));
            }

            return Results.Ok(boardStatus);
        })
        .WithName("ReconnectBoard")
        .WithDescription("Attempt to reconnect a specific relay board");

        // ============================================
        // Channel Configuration Endpoints (per board)
        // ============================================

        // GET /api/triggers/boards/{boardId}/{channel} - Get single channel status
        group.MapGet("/boards/{boardId}/{channel:int}", (
            string boardId,
            int channel,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers/boards/{BoardId}/{Channel}", boardId, channel);

            var boardStatus = service.GetBoardStatus(boardId);
            if (boardStatus == null)
            {
                return Results.NotFound(new ErrorResponse(false, $"Board '{boardId}' not found"));
            }

            if (channel < 1 || channel > boardStatus.ChannelCount)
            {
                return Results.BadRequest(new ErrorResponse(false, $"Channel must be between 1 and {boardStatus.ChannelCount}"));
            }

            var trigger = boardStatus.Triggers.FirstOrDefault(t => t.Channel == channel);
            return trigger != null
                ? Results.Ok(trigger)
                : Results.NotFound(new ErrorResponse(false, $"Channel {channel} not found"));
        })
        .WithName("GetBoardTriggerChannel")
        .WithDescription("Get status of a specific trigger channel on a board");

        // PUT /api/triggers/boards/{boardId}/{channel} - Configure a trigger channel
        group.MapPut("/boards/{boardId}/{channel:int}", (
            string boardId,
            int channel,
            TriggerConfigureRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: PUT /api/triggers/boards/{BoardId}/{Channel}", boardId, channel);

            // Validate offDelaySeconds
            if (request.OffDelaySeconds < 0)
            {
                return Results.BadRequest(new ErrorResponse(false, "Off delay must be 0 or greater"));
            }
            if (request.OffDelaySeconds > 3600)
            {
                return Results.BadRequest(new ErrorResponse(false, "Off delay must not exceed 3600 seconds (1 hour)"));
            }

            try
            {
                var success = service.ConfigureTrigger(
                    boardId,
                    channel,
                    request.CustomSinkName,
                    request.OffDelaySeconds,
                    request.ZoneName);

                var boardStatus = service.GetBoardStatus(boardId);
                var trigger = boardStatus?.Triggers.FirstOrDefault(t => t.Channel == channel);

                return Results.Ok(trigger);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
        })
        .WithName("ConfigureBoardTrigger")
        .WithDescription("Configure a trigger channel mapping on a specific board");

        // DELETE /api/triggers/boards/{boardId}/{channel} - Unassign a trigger channel
        group.MapDelete("/boards/{boardId}/{channel:int}", (
            string boardId,
            int channel,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: DELETE /api/triggers/boards/{BoardId}/{Channel}", boardId, channel);

            try
            {
                service.ConfigureTrigger(boardId, channel, null, 60, null);
                logger.LogInformation("Trigger channel {BoardId}/{Channel} unassigned", boardId, channel);
                return Results.Ok(new SuccessResponse(true, $"Trigger channel {boardId}/{channel} unassigned"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
        })
        .WithName("UnassignBoardTrigger")
        .WithDescription("Unassign a trigger channel on a specific board");

        // POST /api/triggers/boards/test - Manual relay test (query params for boardId with slashes)
        // Using query params because boardId may contain slashes (e.g., MODBUS:/dev/ttyUSB0)
        group.MapPost("/boards/test", (
            [FromQuery(Name = "boardId")] string boardId,
            [FromQuery(Name = "channel")] int channel,
            RelayManualControlRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: POST /api/triggers/boards/test?boardId={BoardId}&channel={Channel} - {On}", boardId, channel, request.On);

            var boardStatus = service.GetBoardStatus(boardId);
            if (boardStatus == null)
            {
                return Results.NotFound(new ErrorResponse(false, $"Board '{boardId}' not found"));
            }

            if (boardStatus.State != TriggerFeatureState.Connected)
            {
                return Results.BadRequest(new ErrorResponse(false,
                    $"Board not connected (state: {boardStatus.State})"));
            }

            try
            {
                var success = service.ManualControl(boardId, channel, request.On);
                if (success)
                {
                    return Results.Ok(new SuccessResponse(true,
                        $"Relay {boardId}/{channel} set to {(request.On ? "ON" : "OFF")}"));
                }
                else
                {
                    return Results.Problem(
                        detail: "Failed to control relay",
                        statusCode: 500,
                        title: "Relay control failed");
                }
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
        })
        .WithName("TestBoardRelayQuery")
        .WithDescription("Manually control a relay for testing (query params - use for board IDs with slashes)");

        // POST /api/triggers/boards/{boardId}/{channel}/test - Manual relay test (path params)
        // For boards without slashes in ID (FTDI, HID) - backward compatible
        group.MapPost("/boards/{boardId}/{channel:int}/test", (
            string boardId,
            int channel,
            RelayManualControlRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            boardId = HttpUtility.UrlDecode(boardId);
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: POST /api/triggers/boards/{BoardId}/{Channel}/test - {On}", boardId, channel, request.On);

            var boardStatus = service.GetBoardStatus(boardId);
            if (boardStatus == null)
            {
                return Results.NotFound(new ErrorResponse(false, $"Board '{boardId}' not found"));
            }

            if (boardStatus.State != TriggerFeatureState.Connected)
            {
                return Results.BadRequest(new ErrorResponse(false,
                    $"Board not connected (state: {boardStatus.State})"));
            }

            try
            {
                var success = service.ManualControl(boardId, channel, request.On);
                if (success)
                {
                    return Results.Ok(new SuccessResponse(true,
                        $"Relay {boardId}/{channel} set to {(request.On ? "ON" : "OFF")}"));
                }
                else
                {
                    return Results.Problem(
                        detail: "Failed to control relay",
                        statusCode: 500,
                        title: "Relay control failed");
                }
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
        })
        .WithName("TestBoardRelay")
        .WithDescription("Manually control a relay for testing on a specific board (path params)");

        // ============================================
        // Legacy Endpoints (for backwards compatibility)
        // These operate on the first configured board
        // ============================================

        // PUT /api/triggers/channels - Update channel count (legacy - updates first board)
        group.MapPut("/channels", (
            ChannelCountRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: PUT /api/triggers/channels (legacy) - {ChannelCount}", request.ChannelCount);

            if (!ValidChannelCounts.IsValid(request.ChannelCount))
            {
                return Results.BadRequest(new ErrorResponse(false,
                    $"Channel count must be one of: {string.Join(", ", ValidChannelCounts.Values)}"));
            }

            var status = service.GetStatus();
            var firstBoard = status.Boards.FirstOrDefault();
            if (firstBoard == null)
            {
                return Results.BadRequest(new ErrorResponse(false, "No boards configured. Add a board first."));
            }

            var success = service.UpdateBoard(firstBoard.BoardId, null, request.ChannelCount);
            status = service.GetStatus();

            if (success)
            {
                logger.LogInformation("Channel count updated to {Count} on board {BoardId}",
                    request.ChannelCount, firstBoard.BoardId);
                return Results.Ok(status);
            }
            else
            {
                return Results.BadRequest(new ErrorResponse(false, "Failed to update channel count"));
            }
        })
        .WithName("SetChannelCount")
        .WithDescription("Update the number of relay channels on the first board (legacy)");

        // GET /api/triggers/{channel} - Get single channel status (legacy - first board)
        group.MapGet("/{channel:int}", (
            int channel,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: GET /api/triggers/{Channel} (legacy)", channel);

            var status = service.GetStatus();
            var firstBoard = status.Boards.FirstOrDefault();
            if (firstBoard == null)
            {
                return Results.NotFound(new ErrorResponse(false, "No boards configured"));
            }

            if (channel < 1 || channel > firstBoard.ChannelCount)
            {
                return Results.BadRequest(new ErrorResponse(false, $"Channel must be between 1 and {firstBoard.ChannelCount}"));
            }

            var trigger = firstBoard.Triggers.FirstOrDefault(t => t.Channel == channel);
            return trigger != null
                ? Results.Ok(trigger)
                : Results.NotFound(new ErrorResponse(false, $"Channel {channel} not found"));
        })
        .WithName("GetTriggerChannel")
        .WithDescription("Get status of a specific trigger channel on the first board (legacy)");

        // PUT /api/triggers/{channel} - Configure a trigger channel (legacy - first board)
        group.MapPut("/{channel:int}", (
            int channel,
            TriggerConfigureRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: PUT /api/triggers/{Channel} (legacy)", channel);

            var status = service.GetStatus();
            var firstBoard = status.Boards.FirstOrDefault();
            if (firstBoard == null)
            {
                return Results.BadRequest(new ErrorResponse(false, "No boards configured. Add a board first."));
            }

            if (request.OffDelaySeconds < 0)
            {
                return Results.BadRequest(new ErrorResponse(false, "Off delay must be 0 or greater"));
            }
            if (request.OffDelaySeconds > 3600)
            {
                return Results.BadRequest(new ErrorResponse(false, "Off delay must not exceed 3600 seconds (1 hour)"));
            }

            try
            {
                var success = service.ConfigureTrigger(
                    firstBoard.BoardId,
                    channel,
                    request.CustomSinkName,
                    request.OffDelaySeconds,
                    request.ZoneName);

                var boardStatus = service.GetBoardStatus(firstBoard.BoardId);
                var trigger = boardStatus?.Triggers.FirstOrDefault(t => t.Channel == channel);

                return Results.Ok(trigger);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
        })
        .WithName("ConfigureTrigger")
        .WithDescription("Configure a trigger channel on the first board (legacy)");

        // DELETE /api/triggers/{channel} - Unassign a trigger channel (legacy - first board)
        group.MapDelete("/{channel:int}", (
            int channel,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: DELETE /api/triggers/{Channel} (legacy)", channel);

            var status = service.GetStatus();
            var firstBoard = status.Boards.FirstOrDefault();
            if (firstBoard == null)
            {
                return Results.BadRequest(new ErrorResponse(false, "No boards configured"));
            }

            try
            {
                service.ConfigureTrigger(firstBoard.BoardId, channel, null, 60, null);
                logger.LogInformation("Trigger channel {Channel} unassigned on board {BoardId}",
                    channel, firstBoard.BoardId);
                return Results.Ok(new SuccessResponse(true, $"Trigger channel {channel} unassigned"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
        })
        .WithName("UnassignTrigger")
        .WithDescription("Unassign a trigger channel on the first board (legacy)");

        // POST /api/triggers/{channel}/test - Manual relay test (legacy - first board)
        group.MapPost("/{channel:int}/test", (
            int channel,
            RelayManualControlRequest request,
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: POST /api/triggers/{Channel}/test (legacy) - {On}", channel, request.On);

            var status = service.GetStatus();
            var firstBoard = status.Boards.FirstOrDefault();
            if (firstBoard == null)
            {
                return Results.BadRequest(new ErrorResponse(false, "No boards configured"));
            }

            if (firstBoard.State != TriggerFeatureState.Connected)
            {
                return Results.BadRequest(new ErrorResponse(false,
                    $"Board not connected (state: {firstBoard.State})"));
            }

            try
            {
                var success = service.ManualControl(firstBoard.BoardId, channel, request.On);
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
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
        })
        .WithName("TestRelay")
        .WithDescription("Manually control a relay on the first board (legacy)");

        // POST /api/triggers/reconnect - Try to reconnect (legacy - reconnects all boards)
        group.MapPost("/reconnect", (
            TriggerService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("TriggersEndpoint");
            logger.LogDebug("API: POST /api/triggers/reconnect (legacy)");

            var status = service.GetStatus();
            if (!status.Enabled)
            {
                return Results.BadRequest(new ErrorResponse(false, "Trigger feature is not enabled"));
            }

            // Re-enable to trigger reconnection of all boards
            service.SetEnabled(true);
            status = service.GetStatus();

            return Results.Ok(status);
        })
        .WithName("ReconnectRelayBoard")
        .WithDescription("Attempt to reconnect all relay boards (legacy)");
    }
}
