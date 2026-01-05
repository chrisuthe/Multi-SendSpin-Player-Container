using System.Text.Json.Serialization;
using MultiRoomAudio.Controllers;
using MultiRoomAudio.Hubs;
using MultiRoomAudio.Services;
using MultiRoomAudio.Utilities;

// Application version - update this for releases
const string AppVersion = "2.0.0";
const string AppName = "Multi-Room Audio Controller";

var builder = WebApplication.CreateBuilder(args);

// Configure logging for HAOS compatibility
// Console logging goes to supervisor logs when running as add-on
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    // Use simple format for better readability in HA logs
    options.FormatterName = "simple";
});
builder.Logging.AddDebug();

// Parse log level from environment (HAOS passes this from config)
var logLevelStr = Environment.GetEnvironmentVariable("LOG_LEVEL")?.ToLower() ?? "info";
var logLevel = logLevelStr switch
{
    "debug" => LogLevel.Debug,
    "trace" => LogLevel.Trace,
    "warning" => LogLevel.Warning,
    "error" => LogLevel.Error,
    _ => LogLevel.Information
};
builder.Logging.SetMinimumLevel(logLevel);

// Reduce noise from framework loggers unless in debug mode
if (logLevel > LogLevel.Debug)
{
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
    builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
}

// Configure services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Multi-Room Audio Controller API",
        Version = "v2",
        Description = "REST API for managing Sendspin audio players. Provides device enumeration, player lifecycle management, and real-time control."
    });
});

// Add SignalR for real-time status updates with string enum serialization
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// Add CORS for web UI and external access
// Note: Wide-open CORS is acceptable here because:
// 1. This runs on a local network or as a Home Assistant add-on (trusted environment)
// 2. The API manages local audio devices with no external authentication
// 3. Restricting CORS would break Home Assistant ingress and local network access
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Add health checks
builder.Services.AddHealthChecks();

// Configure JSON serialization to use string enum values
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Core services (singletons for shared state)
builder.Services.AddSingleton<EnvironmentService>();
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<EnvironmentService>();
    var logger = sp.GetRequiredService<ILogger<AlsaCommandRunner>>();
    return new AlsaCommandRunner(logger, env.UsePulseAudio);
});

// Add PlayerManagerService as singleton and hosted service
builder.Services.AddSingleton<PlayerManagerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlayerManagerService>());

// Static files are served via UseStaticFiles() middleware below

// Configure Kestrel to listen on port 8096 (or PORT env var)
const int DefaultPort = 8096;
var portString = Environment.GetEnvironmentVariable("WEB_PORT")
    ?? Environment.GetEnvironmentVariable("PORT");

int port;
if (string.IsNullOrEmpty(portString))
{
    port = DefaultPort;
}
else if (!int.TryParse(portString, out port) || port < 1 || port > 65535)
{
    var tempLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<Program>();
    tempLogger.LogWarning(
        "Invalid port value '{PortString}' specified. Using default port {DefaultPort}",
        portString, DefaultPort);
    port = DefaultPort;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port);
});

var app = builder.Build();

// Configure middleware pipeline
app.UseCors("AllowAll");

// Serve static files (wwwroot) with no-cache to ensure UI updates are seen
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Disable caching for local static files to ensure UI updates are seen immediately
        // This is appropriate for an admin UI with infrequent access
        ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
    }
});

// Enable Swagger with relative paths for ingress compatibility
app.UseSwagger(c =>
{
    // Make Swagger use relative server URLs so it works behind HA ingress proxy
    c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
    {
        // Check for X-Ingress-Path header (set by HAOS ingress proxy)
        var ingressPath = httpReq.Headers["X-Ingress-Path"].FirstOrDefault();
        if (!string.IsNullOrEmpty(ingressPath))
        {
            // Use the ingress path as the server base URL
            swaggerDoc.Servers = new List<Microsoft.OpenApi.Models.OpenApiServer>
            {
                new() { Url = ingressPath }
            };
        }
        else
        {
            // For direct access, use relative URL (empty = same origin)
            swaggerDoc.Servers = new List<Microsoft.OpenApi.Models.OpenApiServer>
            {
                new() { Url = "/" }
            };
        }
    });
});
app.UseSwaggerUI(c =>
{
    // Use relative path so it works behind HA ingress proxy
    c.SwaggerEndpoint("../swagger/v1/swagger.json", "Multi-Room Audio API v2");
    c.RoutePrefix = "docs";
});

// Map health check endpoints
app.MapHealthChecks("/healthz");

// Map SignalR hub
app.MapHub<PlayerStatusHub>("/hubs/status");

// Map API endpoints
app.MapHealthEndpoints();
app.MapPlayersEndpoints();
app.MapDevicesEndpoints();
app.MapProvidersEndpoints();

// Root endpoint redirects to index.html or shows API info
app.MapGet("/api", () => Results.Ok(new
{
    service = "multi-room-audio",
    description = "Sendspin-only Multi-Room Audio Controller",
    version = AppVersion,
    endpoints = new
    {
        health = "/api/health",
        players = "/api/players",
        devices = "/api/devices",
        providers = "/api/providers",
        swagger = "/docs"
    }
}))
.WithTags("Info")
.WithName("ApiInfo");

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var environmentService = app.Services.GetRequiredService<EnvironmentService>();

// Startup banner - visible in HAOS supervisor logs
logger.LogInformation("========================================");
logger.LogInformation("{AppName} v{Version}", AppName, AppVersion);
logger.LogInformation("========================================");
logger.LogInformation("Environment: {Environment}", environmentService.EnvironmentName);
logger.LogInformation("Log level: {LogLevel}", logLevelStr);
logger.LogInformation("Web port: {Port}", port);
logger.LogInformation("Config path: {ConfigPath}", environmentService.ConfigPath);
logger.LogInformation("Log path: {LogPath}", environmentService.LogPath);
logger.LogInformation("Audio backend: {AudioBackend}", environmentService.AudioBackend);

if (environmentService.IsHaos)
{
    logger.LogInformation("Running as Home Assistant add-on");
    logger.LogDebug("Supervisor token present: {HasToken}",
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")));

    // Log PulseAudio diagnostic info
    var pulseServer = Environment.GetEnvironmentVariable("PULSE_SERVER");
    logger.LogInformation("PULSE_SERVER: {PulseServer}", pulseServer ?? "(not set)");

    // Check for PulseAudio socket
    var pulseSocketPaths = new[] { "/run/pulse", "/var/run/pulse", "/tmp/pulse" };
    foreach (var path in pulseSocketPaths)
    {
        if (Directory.Exists(path))
        {
            try
            {
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                logger.LogInformation("PulseAudio socket directory {Path}: {FileCount} files", path, files.Length);
                foreach (var file in files.Take(5))
                {
                    logger.LogDebug("  {File}", file);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not enumerate {Path}: {Error}", path, ex.Message);
            }
        }
    }

    // Try to run pactl info for diagnostics
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pactl",
            Arguments = "info",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process != null)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode == 0)
            {
                logger.LogInformation("PulseAudio connected successfully");
                // Log key info lines
                foreach (var line in output.Split('\n').Where(l =>
                    l.StartsWith("Server Name:") ||
                    l.StartsWith("Default Sink:") ||
                    l.StartsWith("Default Source:")))
                {
                    logger.LogInformation("  {Line}", line.Trim());
                }
            }
            else
            {
                logger.LogWarning("PulseAudio connection failed: {Error}", error.Trim());
            }
        }

        // Also list available sinks
        psi.Arguments = "list sinks short";
        using var sinkProcess = System.Diagnostics.Process.Start(psi);
        if (sinkProcess != null)
        {
            var sinkOutput = sinkProcess.StandardOutput.ReadToEnd();
            sinkProcess.WaitForExit(5000);

            if (sinkProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(sinkOutput))
            {
                logger.LogInformation("PulseAudio sinks available:");
                foreach (var line in sinkOutput.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                {
                    logger.LogInformation("  {Sink}", line.Trim());
                }
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning("Could not run pactl diagnostics: {Error}", ex.Message);
    }
}
else
{
    logger.LogInformation("Running in standalone Docker mode");
}

logger.LogInformation("API documentation available at /docs");
logger.LogInformation("========================================");

app.Run();
