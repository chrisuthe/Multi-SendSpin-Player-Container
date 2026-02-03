using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for log retrieval and management.
/// </summary>
public static class LogsEndpoint
{
    /// <summary>
    /// Registers log retrieval and management API endpoints with the application.
    /// </summary>
    /// <remarks>
    /// Endpoints:
    /// <list type="bullet">
    /// <item>GET /api/logs - Query logs with filtering (level, category, search, date range)</item>
    /// <item>GET /api/logs/stats - Get log statistics by level and category</item>
    /// <item>GET /api/logs/download - Download all logs as a text file</item>
    /// <item>DELETE /api/logs - Clear all logs from memory and files</item>
    /// </list>
    /// </remarks>
    /// <param name="app">The WebApplication to register endpoints on.</param>
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

        // GET /api/logs/download - Download all logs as text file
        group.MapGet("/download", (
            string? level,
            string? category,
            string? search,
            DateTime? start,
            DateTime? end,
            LoggingService loggingService,
            HttpContext context) =>
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

            // Get all matching entries (no pagination limit)
            var options = new LogQueryOptions(
                MinLevel: minLevel,
                Category: logCategory,
                SearchText: search,
                StartTime: start,
                EndTime: end,
                Skip: 0,
                Take: int.MaxValue,
                NewestFirst: false // Oldest first for log file
            );

            var entries = loggingService.GetEntries(options);

            // Build text content in same format as frontend: timestamp|level|category|message[|exception]
            var content = string.Join("\n", entries.Select(e =>
            {
                var dto = e.ToDto();
                var line = $"{dto.Timestamp}|{dto.Level}|{dto.Category}|{dto.Message}";
                if (!string.IsNullOrEmpty(dto.Exception))
                {
                    line += $"|{dto.Exception}";
                }
                return line;
            }));

            // Set response headers for file download
            var filename = $"multiroom-audio-logs-{DateTime.UtcNow:yyyy-MM-dd}.txt";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";

            return Results.Text(content, "text/plain");
        })
        .WithName("DownloadLogs")
        .WithDescription("Download all logs as a text file with optional filtering");

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
