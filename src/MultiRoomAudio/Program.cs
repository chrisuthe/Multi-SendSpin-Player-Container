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

// Add SignalR for real-time status updates
builder.Services.AddSignalR();

// Add CORS for web UI and external access
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

// Serve static files from wwwroot
builder.Services.AddDirectoryBrowser();

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

// Serve static files (wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

// Enable Swagger
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Multi-Room Audio API v2");
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
    version = "2.0.0",
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
}
else
{
    logger.LogInformation("Running in standalone Docker mode");
}

logger.LogInformation("API documentation available at /docs");
logger.LogInformation("========================================");

app.Run();
