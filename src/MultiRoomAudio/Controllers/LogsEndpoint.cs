using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for log retrieval and management.
/// </summary>
public static class LogsEndpoint
{
    public static void MapLogsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/logs")
            .WithTags("Logs")
            .WithOpenApi();

        // GET /api/logs - Query logs with filtering
        group.MapGet("/", (
            string? level,
            string? category,
            string? search,
            DateTime? start,
            DateTime? end,
            int skip,
            int take,
            bool newestFirst,
            LoggingService loggingService) =>
        {
            // Parse level filter
            LogLevel? minLevel = null;
            if (!string.IsNullOrEmpty(level))
            {
                if (Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsedLevel))
                {
                    minLevel = parsedLevel;
                }
            }

            // Parse category filter
            LogCategory? logCategory = null;
            if (!string.IsNullOrEmpty(category))
            {
                if (Enum.TryParse<LogCategory>(category, ignoreCase: true, out var parsedCategory))
                {
                    logCategory = parsedCategory;
                }
            }

            // Ensure reasonable defaults
            if (take <= 0)
                take = 100;
            if (take > 500)
                take = 500;
            if (skip < 0)
                skip = 0;

            var options = new LogQueryOptions(
                MinLevel: minLevel,
                Category: logCategory,
                SearchText: search,
                StartTime: start,
                EndTime: end,
                Skip: skip,
                Take: take,
                NewestFirst: newestFirst
            );

            var entries = loggingService.GetEntries(options);
            var totalCount = loggingService.GetTotalCount(options);

            var response = new LogsResponse(
                Entries: entries.Select(e => e.ToDto()).ToList(),
                TotalCount: totalCount,
                Skip: skip,
                Take: take
            );

            return Results.Ok(response);
        })
        .WithName("GetLogs")
        .WithDescription("Query application logs with optional filtering by level, category, and search text");

        // GET /api/logs/stats - Get log statistics
        group.MapGet("/stats", (LoggingService loggingService) =>
        {
            var stats = loggingService.GetStats();
            return Results.Ok(stats.ToResponse());
        })
        .WithName("GetLogStats")
        .WithDescription("Get log statistics by level and category");

        // DELETE /api/logs - Clear all logs
        group.MapDelete("/", (LoggingService loggingService) =>
        {
            loggingService.Clear();
            return Results.Ok(new { success = true, message = "Logs cleared" });
        })
        .WithName("ClearLogs")
        .WithDescription("Clear all logs from memory and files");
    }
}
