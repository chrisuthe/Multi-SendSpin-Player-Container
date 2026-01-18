using MultiRoomAudio.Models;
using MultiRoomAudio.Services;
using MultiRoomAudio.Utilities;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for custom PulseAudio sink management.
/// </summary>
public static class SinksEndpoint
{
    #region Helper Methods

    /// <summary>
    /// Creates a standardized NotFound response for missing sinks.
    /// </summary>
    private static IResult SinkNotFoundResult(string name, ILogger logger, string action)
    {
        logger.LogDebug("API: Sink {SinkName} not found for {Action}", name, action);
        return Results.NotFound(new ErrorResponse(false, $"Custom sink '{name}' not found"));
    }

    #endregion

    public static void MapSinksEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sinks")
            .WithTags("Custom Sinks")
            .WithOpenApi();

        // GET /api/sinks - List all custom sinks
        group.MapGet("/", (CustomSinksService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: GET /api/sinks");
            var response = service.GetAllSinks();
            logger.LogDebug("API: Returning {SinkCount} custom sinks", response.Count);
            return Results.Ok(response);
        })
        .WithName("ListCustomSinks")
        .WithDescription("List all custom PulseAudio sinks");

        // GET /api/sinks/channels - Get available channel names for UI
        group.MapGet("/channels", () =>
        {
            return Results.Ok(new
            {
                stereo = PulseAudioChannels.StereoChannels,
                quad = PulseAudioChannels.QuadChannels,
                surround51 = PulseAudioChannels.Surround51Channels,
                surround71 = PulseAudioChannels.Surround71Channels,
                all = PulseAudioChannels.AllChannels
            });
        })
        .WithName("GetChannelNames")
        .WithDescription("Get available PulseAudio channel names for remap-sink configuration");

        // GET /api/sinks/{name} - Get specific sink
        group.MapGet("/{name}", (string name, CustomSinksService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: GET /api/sinks/{SinkName}", name);
            var sink = service.GetSink(name);
            if (sink == null)
                return SinkNotFoundResult(name, logger, "get");

            return Results.Ok(sink);
        })
        .WithName("GetCustomSink")
        .WithDescription("Get details of a specific custom sink");

        // POST /api/sinks/combine - Create combine-sink
        group.MapPost("/combine", async (
            CombineSinkCreateRequest request,
            CustomSinksService service,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: POST /api/sinks/combine - Creating {SinkName}", request.Name);
            try
            {
                var sink = await service.CreateCombineSinkAsync(request, ct);
                logger.LogInformation("API: Combine-sink {SinkName} created successfully", sink.Name);
                return Results.Created($"/api/sinks/{sink.Name}", sink);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                logger.LogWarning("API: Sink creation conflict - {Message}", ex.Message);
                return Results.Conflict(new ErrorResponse(false, ex.Message));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning("API: Sink creation bad request - {Message}", ex.Message);
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to create combine-sink {SinkName}", request.Name);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to create combine-sink");
            }
        })
        .WithName("CreateCombineSink")
        .WithDescription("Create a combine-sink to merge multiple audio outputs");

        // POST /api/sinks/remap - Create remap-sink
        group.MapPost("/remap", async (
            RemapSinkCreateRequest request,
            CustomSinksService service,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: POST /api/sinks/remap - Creating {SinkName}", request.Name);
            try
            {
                var sink = await service.CreateRemapSinkAsync(request, ct);
                logger.LogInformation("API: Remap-sink {SinkName} created successfully", sink.Name);
                return Results.Created($"/api/sinks/{sink.Name}", sink);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                logger.LogWarning("API: Sink creation conflict - {Message}", ex.Message);
                return Results.Conflict(new ErrorResponse(false, ex.Message));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning("API: Sink creation bad request - {Message}", ex.Message);
                return Results.BadRequest(new ErrorResponse(false, ex.Message));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to create remap-sink {SinkName}", request.Name);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to create remap-sink");
            }
        })
        .WithName("CreateRemapSink")
        .WithDescription("Create a remap-sink to extract channels from a multi-channel device");

        // DELETE /api/sinks/{name} - Delete sink
        group.MapDelete("/{name}", async (
            string name,
            CustomSinksService service,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: DELETE /api/sinks/{SinkName}", name);
            var deleted = await service.DeleteSinkAsync(name, ct);
            if (!deleted)
                return SinkNotFoundResult(name, logger, "delete");

            logger.LogInformation("API: Sink {SinkName} deleted successfully", name);
            return Results.Ok(new SuccessResponse(true, $"Sink '{name}' deleted"));
        })
        .WithName("DeleteCustomSink")
        .WithDescription("Delete a custom sink");

        // GET /api/sinks/{name}/status - Check if sink is loaded
        group.MapGet("/{name}/status", async (
            string name,
            CustomSinksService service,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: GET /api/sinks/{SinkName}/status", name);
            var sink = service.GetSink(name);
            if (sink == null)
                return SinkNotFoundResult(name, logger, "status");

            var isLoaded = await service.IsSinkLoadedAsync(name, ct);
            return Results.Ok(new
            {
                name,
                isLoaded,
                moduleIndex = sink.ModuleIndex,
                state = sink.State.ToString()
            });
        })
        .WithName("GetSinkStatus")
        .WithDescription("Check if a custom sink is currently loaded in PulseAudio");

        // POST /api/sinks/{name}/reload - Reload a sink
        group.MapPost("/{name}/reload", async (
            string name,
            CustomSinksService service,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: POST /api/sinks/{SinkName}/reload", name);
            var sink = await service.ReloadSinkAsync(name, ct);
            if (sink == null)
                return SinkNotFoundResult(name, logger, "reload");

            logger.LogInformation("API: Sink {SinkName} reloaded", name);
            return Results.Ok(sink);
        })
        .WithName("ReloadCustomSink")
        .WithDescription("Unload and reload a custom sink");

        // POST /api/sinks/{name}/test-tone - Play test tone through custom sink
        group.MapPost("/{name}/test-tone", async (
            string name,
            TestToneRequest? request,
            CustomSinksService service,
            ToneGeneratorService toneGenerator,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: POST /api/sinks/{SinkName}/test-tone", name);

            var sink = service.GetSink(name);
            if (sink == null)
                return SinkNotFoundResult(name, logger, "test-tone");

            if (sink.State != CustomSinkState.Loaded)
            {
                logger.LogWarning("API: Cannot play test tone - sink {SinkName} is not loaded (state: {State})", name, sink.State);
                return Results.BadRequest(new ErrorResponse(false, $"Sink '{name}' is not loaded (state: {sink.State})"));
            }

            if (string.IsNullOrEmpty(sink.PulseAudioSinkName))
            {
                logger.LogWarning("API: Cannot play test tone - sink {SinkName} has no PulseAudio sink name", name);
                return Results.BadRequest(new ErrorResponse(false, $"Sink '{name}' has no PulseAudio sink name"));
            }

            try
            {
                await toneGenerator.PlayTestToneAsync(
                    sink.PulseAudioSinkName,
                    frequencyHz: request?.FrequencyHz ?? 1000,
                    durationMs: request?.DurationMs ?? 1500,
                    ct: ct);

                return Results.Ok(new
                {
                    success = true,
                    message = "Test tone played successfully",
                    sinkName = name,
                    pulseAudioSinkName = sink.PulseAudioSinkName,
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
                logger.LogWarning(ex, "Test tone playback timed out for sink {SinkName}", name);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 504,
                    title: "Test tone playback timed out");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to play test tone on sink {SinkName}", name);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to play test tone");
            }
        })
        .WithName("PlaySinkTestTone")
        .WithDescription("Play a test tone through a custom sink for identification");

        // GET /api/sinks/import/scan - Scan default.pa for importable sinks
        group.MapGet("/import/scan", (DefaultPaParser parser, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: GET /api/sinks/import/scan");

            if (!parser.IsAvailable())
            {
                return Results.Ok(new ImportScanResponse(0, []));
            }

            var detected = parser.ScanForSinks();
            var sinks = detected.Select(s => new DetectedSinkInfo(
                LineNumber: s.LineNumber,
                Type: s.Type.ToString(),
                Name: s.SinkName,
                Description: s.Description,
                Slaves: s.Slaves,
                MasterSink: s.MasterSink,
                Preview: s.RawLine.Length > 80 ? s.RawLine[..80] + "..." : s.RawLine
            )).ToList();

            logger.LogDebug("API: Found {Count} importable sinks", sinks.Count);
            return Results.Ok(new ImportScanResponse(sinks.Count, sinks));
        })
        .WithName("ScanDefaultPa")
        .WithDescription("Scan /etc/pulse/default.pa for importable combine-sink and remap-sink definitions");

        // GET /api/sinks/import/status - Check if default.pa is available and writable
        group.MapGet("/import/status", (DefaultPaParser parser) =>
        {
            return Results.Ok(new
            {
                available = parser.IsAvailable(),
                writable = parser.IsWritable()
            });
        })
        .WithName("GetImportStatus")
        .WithDescription("Check if default.pa is available and writable for import operations");

        // POST /api/sinks/import - Import selected sinks from default.pa
        group.MapPost("/import", async (
            ImportSinksRequest request,
            DefaultPaParser parser,
            CustomSinksService service,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("SinksEndpoint");
            logger.LogDebug("API: POST /api/sinks/import - Importing {Count} sinks", request.LineNumbers.Count);

            if (!parser.IsAvailable())
            {
                return Results.BadRequest(new ErrorResponse(false, "default.pa is not available"));
            }

            var detected = parser.ScanForSinks();
            var imported = new List<string>();
            var errors = new List<string>();

            foreach (var lineNum in request.LineNumbers)
            {
                var sink = detected.FirstOrDefault(s => s.LineNumber == lineNum);
                if (sink == null)
                {
                    errors.Add($"Line {lineNum}: not found");
                    continue;
                }

                try
                {
                    // Import into app management
                    await service.ImportSinkAsync(sink, ct);

                    // Comment out in default.pa (handles multi-line entries with continuations)
                    if (!parser.CommentOutLines(sink.LineNumber, sink.EndLineNumber))
                    {
                        logger.LogWarning("Failed to comment out lines {Start}-{End} in default.pa",
                            sink.LineNumber, sink.EndLineNumber);
                    }

                    imported.Add(sink.SinkName);
                    logger.LogInformation("Imported sink '{Name}' from default.pa line {Line}",
                        sink.SinkName, lineNum);
                }
                catch (Exception ex)
                {
                    errors.Add($"{sink.SinkName}: {ex.Message}");
                    logger.LogError(ex, "Failed to import sink '{Name}'", sink.SinkName);
                }
            }

            return Results.Ok(new ImportResultResponse(imported, errors));
        })
        .WithName("ImportSinks")
        .WithDescription("Import selected sinks from default.pa into app management");
    }
}
