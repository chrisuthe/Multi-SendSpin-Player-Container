using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Audio;
using MultiRoomAudio.Controllers;
using MultiRoomAudio.Hubs;
using MultiRoomAudio.Logging;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;
using MultiRoomAudio.Utilities;

// Application version from build environment (set by Docker build args)
var appVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";
var buildSha = Environment.GetEnvironmentVariable("APP_BUILD_SHA");
var buildDate = Environment.GetEnvironmentVariable("APP_BUILD_DATE");
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
builder.Services.AddSingleton<LoggingService>();
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<VolumeCommandRunner>();
builder.Services.AddSingleton<BackendFactory>();
builder.Services.AddSingleton<DeviceMatchingService>();

// Onboarding services
builder.Services.AddSingleton<ToneGeneratorService>();
builder.Services.AddSingleton<OnboardingService>();

// Add PulseAudio utilities (no startup dependency)
// Use mock implementations when MOCK_HARDWARE is enabled
// Check both environment variable (Docker) and HAOS options file (add-on UI toggle)
var isMockHardware = Environment.GetEnvironmentVariable("MOCK_HARDWARE")?.ToLower() == "true";
if (!isMockHardware)
{
    // Check HAOS options.json for mock_hardware toggle
    const string haosOptionsFile = "/data/options.json";
    if (File.Exists(haosOptionsFile))
    {
        try
        {
            var json = File.ReadAllText(haosOptionsFile);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mock_hardware", out var mockProp) &&
                mockProp.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                isMockHardware = true;
            }
        }
        catch
        {
            // Ignore parsing errors - will use default (false)
        }
    }
}
if (isMockHardware)
{
    // Mock hardware configuration service - loads from mock_hardware.yaml if present
    builder.Services.AddSingleton<MultiRoomAudio.Services.MockHardwareConfigService>();

    builder.Services.AddSingleton<MultiRoomAudio.Utilities.IPaModuleRunner, MultiRoomAudio.Audio.Mock.MockPaModuleRunner>();
    // Relay hardware abstractions - mock implementations
    builder.Services.AddSingleton<MultiRoomAudio.Relay.IRelayDeviceEnumerator, MultiRoomAudio.Relay.MockRelayDeviceEnumerator>();
    builder.Services.AddSingleton<MultiRoomAudio.Relay.IRelayBoardFactory, MultiRoomAudio.Relay.MockRelayBoardFactory>();
}
else
{
    builder.Services.AddSingleton<MultiRoomAudio.Utilities.IPaModuleRunner, MultiRoomAudio.Utilities.PaModuleRunner>();
    // Relay hardware abstractions - real implementations
    builder.Services.AddSingleton<MultiRoomAudio.Relay.IRelayDeviceEnumerator, MultiRoomAudio.Relay.RealRelayDeviceEnumerator>();
    builder.Services.AddSingleton<MultiRoomAudio.Relay.IRelayBoardFactory, MultiRoomAudio.Relay.RealRelayBoardFactory>();
}
builder.Services.AddSingleton<DefaultPaParser>();

// Startup diagnostics service
builder.Services.AddSingleton<StartupDiagnosticsService>();

// IMPORTANT: Hosted services start in registration order.
// Correct order: CardProfiles → CustomSinks → Players
// - Card profiles must be set before sinks can use surround channels
// - Sinks must exist before players can use them

// 1. CardProfileService - restore saved card profiles (e.g., surround 7.1) FIRST
builder.Services.AddSingleton<CardProfileService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CardProfileService>());

// 2. CustomSinksService - load remap/combine sinks SECOND (depends on profiles)
builder.Services.AddSingleton<CustomSinksService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CustomSinksService>());

// 3. PlayerManagerService - autostart players LAST (depends on sinks existing)
builder.Services.AddSingleton<PlayerManagerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlayerManagerService>());

// 4. TriggerService - 12V relay trigger control (depends on sinks for mapping)
builder.Services.AddSingleton<TriggerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TriggerService>());

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

// Clear default URLs to avoid "Overriding address" warning
builder.WebHost.UseUrls();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port);
});

var app = builder.Build();

// Wire up mock hardware config service to static enumerators if in mock mode
if (isMockHardware)
{
    var mockConfigService = app.Services.GetService<MultiRoomAudio.Services.MockHardwareConfigService>();
    if (mockConfigService != null)
    {
        MultiRoomAudio.Audio.Mock.MockCardEnumerator.SetConfigService(mockConfigService);
    }
}

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

// Map SignalR hubs
app.MapHub<PlayerStatusHub>("/hubs/status");
app.MapHub<LogStreamHub>("/hubs/logs");

// Map API endpoints
app.MapHealthEndpoints();
app.MapPlayersEndpoints();
app.MapDevicesEndpoints();
app.MapProvidersEndpoints();
app.MapSinksEndpoints();
app.MapOnboardingEndpoints();
app.MapCardsEndpoints();
app.MapLogsEndpoints();
app.MapTriggersEndpoints();

// Root endpoint redirects to index.html or shows API info
app.MapGet("/api", () => Results.Ok(new
{
    service = "multi-room-audio",
    description = "Sendspin-only Multi-Room Audio Controller",
    version = appVersion,
    build = buildSha,
    endpoints = new
    {
        health = "/api/health",
        players = "/api/players",
        devices = "/api/devices",
        providers = "/api/providers",
        sinks = "/api/sinks",
        cards = "/api/cards",
        logs = "/api/logs",
        triggers = "/api/triggers",
        swagger = "/docs"
    }
}))
.WithTags("Info")
.WithName("ApiInfo");

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var environmentService = app.Services.GetRequiredService<EnvironmentService>();

// Set up custom logging provider for web-visible logs
var loggingService = app.Services.GetRequiredService<LoggingService>();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
loggerFactory.AddProvider(new WebLoggingProvider(loggingService, logLevel));

// Wire up log streaming via SignalR
var logHubContext = app.Services.GetRequiredService<IHubContext<LogStreamHub>>();
loggingService.LogEntryAdded += async (sender, entry) =>
{
    try
    {
        await logHubContext.BroadcastLogEntryAsync(entry.ToDto());
    }
    catch
    {
        // Don't let log streaming failures crash the app
    }
};

// Startup banner - visible in HAOS supervisor logs
logger.LogInformation("========================================");
logger.LogInformation("{AppName} v{Version}", AppName, appVersion);
if (!string.IsNullOrEmpty(buildSha) && buildSha != "unknown")
{
    logger.LogInformation("Build: {BuildSha}", buildSha);
}
if (!string.IsNullOrEmpty(buildDate) && buildDate != "unknown")
{
    logger.LogInformation("Built: {BuildDate}", buildDate);
}
logger.LogInformation("========================================");
logger.LogInformation("Environment: {Environment}", environmentService.EnvironmentName);
logger.LogInformation("Log level: {LogLevel}", logLevelStr);
logger.LogInformation("Web port: {Port}", port);
logger.LogInformation("Config path: {ConfigPath}", environmentService.ConfigPath);
logger.LogInformation("Log path: {LogPath}", environmentService.LogPath);
logger.LogInformation("Audio backend: {AudioBackend}", environmentService.AudioBackend);

// Run PulseAudio diagnostics (logs environment-specific info)
var diagnosticsService = app.Services.GetRequiredService<StartupDiagnosticsService>();
diagnosticsService.RunPulseAudioDiagnostics();

logger.LogInformation("API documentation available at /docs");
logger.LogInformation("========================================");

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
