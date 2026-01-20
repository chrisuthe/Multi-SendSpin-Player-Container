using System.ComponentModel.DataAnnotations;
using MultiRoomAudio.Audio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;
using MultiRoomAudio.Utilities;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for audio device enumeration.
/// Uses PulseAudio backend for device discovery and playback.
/// </summary>
public static class DevicesEndpoint
{
    /// <summary>
    /// Registers audio device enumeration and configuration API endpoints with the application.
    /// </summary>
    /// <remarks>
    /// Endpoints:
    /// <list type="bullet">
    /// <item>GET /api/devices - List all audio output devices</item>
    /// <item>GET /api/devices/default - Get default device</item>
    /// <item>GET /api/devices/{id} - Get specific device</item>
    /// <item>GET /api/devices/{id}/capabilities - Get device audio format capabilities</item>
    /// <item>GET /api/devices/aliases - Get all device aliases</item>
    /// <item>POST /api/devices/refresh - Re-enumerate audio devices</item>
    /// <item>POST /api/devices/rematch - Force device re-matching</item>
    /// <item>PUT /api/devices/{id}/alias - Set device alias</item>
    /// <item>PUT /api/devices/{id}/hidden - Set device visibility</item>
    /// <item>PUT /api/devices/{id}/max-volume - Set device max volume limit</item>
    /// </list>
    /// </remarks>
    /// <param name="app">The WebApplication to register endpoints on.</param>
    public static void MapDevicesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/devices")
            .WithTags("Devices")
            .WithOpenApi();

        // GET /api/devices - List all output devices (enriched with aliases)
        group.MapGet("/", (
            BackendFactory backendFactory,
            DeviceMatchingService matchingService,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: GET /api/devices - Enumerating audio devices via {Backend} backend",
                backendFactory.BackendName);
            return ApiExceptionHandler.Execute(() =>
            {
                // Get devices enriched with aliases
                var devices = matchingService.GetEnrichedDevices().ToList();
                logger.LogInformation("Audio device enumeration found {DeviceCount} output devices", devices.Count);

                if (devices.Count == 0)
                {
                    logger.LogWarning("No audio output devices detected. Check audio hardware and drivers");
                }
                else
                {
                    var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);
                    logger.LogDebug("Default audio device: {DefaultDevice}",
                        defaultDevice?.Name ?? "(none)");
                }

                return Results.Ok(new
                {
                    devices,
                    count = devices.Count,
                    defaultDevice = devices.FirstOrDefault(d => d.IsDefault)?.Id,
                    backend = backendFactory.BackendName
                });
            }, logger, "enumerate devices");
        })
        .WithName("ListDevices")
        .WithDescription("List all available audio output devices with aliases");

        // GET /api/devices/default - Get default device
        // NOTE: This route must be registered BEFORE /{id} to prevent the parameterized route from intercepting it
        group.MapGet("/default", (BackendFactory backendFactory, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: GET /api/devices/default");
            return ApiExceptionHandler.Execute(() =>
            {
                var device = backendFactory.GetDefaultDevice();
                if (device == null)
                {
                    logger.LogWarning("No default audio output device found");
                    return Results.NotFound(new ErrorResponse(false, "No default output device found"));
                }

                logger.LogDebug("Default device: {DeviceName} (index {DeviceIndex})",
                    device.Name, device.Index);
                return Results.Ok(device);
            }, logger, "get default device");
        })
        .WithName("GetDefaultDevice")
        .WithDescription("Get the default audio output device");

        // GET /api/devices/{id} - Get specific device
        group.MapGet("/{id}", (string id, DeviceMatchingService matchingService, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: GET /api/devices/{DeviceId}", id);
            return ApiExceptionHandler.Execute(() =>
            {
                var device = matchingService.GetEnrichedDevice(id);
                if (device == null)
                {
                    logger.LogDebug("Device {DeviceId} not found", id);
                    return Results.NotFound(new ErrorResponse(false, $"Device '{id}' not found"));
                }
                return Results.Ok(device);
            }, logger, "get device", id);
        })
        .WithName("GetDevice")
        .WithDescription("Get details of a specific audio device");

        // GET /api/devices/{id}/capabilities - Get device audio capabilities
        group.MapGet("/{id}/capabilities", (string id, BackendFactory backendFactory, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: GET /api/devices/{DeviceId}/capabilities", id);
            return ApiExceptionHandler.Execute(() =>
            {
                var capabilities = backendFactory.GetDeviceCapabilities(id);
                if (capabilities == null)
                {
                    logger.LogDebug("Could not query capabilities for device {DeviceId}", id);
                    return Results.NotFound(new ErrorResponse(false, $"Could not query capabilities for device '{id}'"));
                }

                logger.LogDebug("Device {DeviceId} capabilities: rates=[{Rates}], depths=[{Depths}]",
                    id,
                    string.Join(",", capabilities.SupportedSampleRates),
                    string.Join(",", capabilities.SupportedBitDepths));

                return Results.Ok(new
                {
                    deviceId = id,
                    capabilities.SupportedSampleRates,
                    capabilities.SupportedBitDepths,
                    capabilities.MaxChannels,
                    capabilities.PreferredSampleRate,
                    capabilities.PreferredBitDepth
                });
            }, logger, "get device capabilities", id);
        })
        .WithName("GetDeviceCapabilities")
        .WithDescription("Get audio format capabilities of a specific device (sample rates, bit depths)");

        // POST /api/devices/refresh - Refresh device list
        group.MapPost("/refresh", (
            BackendFactory backendFactory,
            DeviceMatchingService matchingService,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: POST /api/devices/refresh");
            return ApiExceptionHandler.Execute(() =>
            {
                logger.LogInformation("Refreshing audio device list via {Backend} backend...",
                    backendFactory.BackendName);
                backendFactory.RefreshDevices();
                var devices = matchingService.GetEnrichedDevices().ToList();

                logger.LogInformation("Audio device refresh complete. Found {DeviceCount} devices", devices.Count);

                // Log each device at debug level for troubleshooting
                foreach (var device in devices)
                {
                    logger.LogDebug("Device {Index}: {Name} (channels: {Channels}, rate: {SampleRate}Hz){Default}",
                        device.Index, device.Name, device.MaxChannels, device.DefaultSampleRate,
                        device.IsDefault ? " [DEFAULT]" : "");
                }

                return Results.Ok(new
                {
                    message = "Device list refreshed",
                    devices,
                    count = devices.Count,
                    backend = backendFactory.BackendName
                });
            }, logger, "refresh devices");
        })
        .WithName("RefreshDevices")
        .WithDescription("Re-enumerate audio devices (detect newly connected USB devices)");

        // GET /api/devices/aliases - Get all device aliases
        group.MapGet("/aliases", (ConfigurationService config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: GET /api/devices/aliases");

            var aliases = config.GetAllDeviceAliases();
            return Results.Ok(new
            {
                aliases,
                count = aliases.Count
            });
        })
        .WithName("GetAllAliases")
        .WithDescription("Get all device aliases");

        // POST /api/devices/rematch - Force device re-matching
        group.MapPost("/rematch", (
            DeviceMatchingService matchingService,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: POST /api/devices/rematch");
            return ApiExceptionHandler.Execute(() =>
            {
                var results = matchingService.MatchAllDevices();
                var updatedPlayers = matchingService.UpdatePlayerDevices();

                return Results.Ok(new
                {
                    message = "Device re-matching complete",
                    matchResults = results,
                    updatedPlayers,
                    devicesMatched = results.Count(r => r.CurrentSinkName != null),
                    devicesUnmatched = results.Count(r => r.CurrentSinkName == null),
                    playersUpdated = updatedPlayers.Count
                });
            }, logger, "re-match devices");
        })
        .WithName("RematchDevices")
        .WithDescription("Re-match persisted device configurations with current sinks and update player configs");

        // PUT /api/devices/{id}/alias - Set device alias
        group.MapPut("/{id}/alias", (
            string id,
            DeviceAliasRequest request,
            BackendFactory backendFactory,
            ConfigurationService config,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: PUT /api/devices/{DeviceId}/alias", id);
            return ApiExceptionHandler.Execute(() =>
            {
                // Find the device
                var device = backendFactory.GetDevice(id);
                if (device == null)
                    return Results.NotFound(new ErrorResponse(false, $"Device '{id}' not found"));

                // Generate stable key and set alias
                var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                config.SetDeviceAlias(deviceKey, request.Alias, device);

                logger.LogInformation("Set alias for device {DeviceId}: '{Alias}'", id, request.Alias ?? "(cleared)");

                return Results.Ok(new
                {
                    success = true,
                    deviceId = id,
                    deviceKey,
                    alias = request.Alias,
                    message = request.Alias != null
                        ? $"Alias set to '{request.Alias}'"
                        : "Alias cleared"
                });
            }, logger, "set device alias", id);
        })
        .WithName("SetDeviceAlias")
        .WithDescription("Set a user-friendly alias for a device");

        // PUT /api/devices/{id}/hidden - Set device hidden status
        group.MapPut("/{id}/hidden", (
            string id,
            DeviceHiddenRequest request,
            BackendFactory backendFactory,
            ConfigurationService config,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: PUT /api/devices/{DeviceId}/hidden", id);
            return ApiExceptionHandler.Execute(() =>
            {
                // Find the device
                var device = backendFactory.GetDevice(id);
                if (device == null)
                    return Results.NotFound(new ErrorResponse(false, $"Device '{id}' not found"));

                // Generate stable key and set hidden status
                var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                config.SetDeviceHidden(deviceKey, request.Hidden, device);

                logger.LogInformation("Set hidden status for device {DeviceId}: {Hidden}", id, request.Hidden);

                return Results.Ok(new
                {
                    success = true,
                    deviceId = id,
                    deviceKey,
                    hidden = request.Hidden,
                    message = request.Hidden
                        ? "Device hidden from player creation"
                        : "Device unhidden"
                });
            }, logger, "set device hidden status", id);
        })
        .WithName("SetDeviceHidden")
        .WithDescription("Set whether a device is hidden from player creation");

        // PUT /api/devices/{id}/max-volume - Set maximum volume limit
        group.MapPut("/{id}/max-volume", async (
            string id,
            DeviceMaxVolumeRequest request,
            BackendFactory backendFactory,
            ConfigurationService config,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: PUT /api/devices/{DeviceId}/max-volume", id);
            return await ApiExceptionHandler.ExecuteAsync(async () =>
            {
                // Find the device
                var device = backendFactory.GetDevice(id);
                if (device == null)
                    return Results.NotFound(new ErrorResponse(false, $"Device '{id}' not found"));

                // Generate stable key and set max volume in config
                var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                config.SetDeviceMaxVolume(deviceKey, request.MaxVolume, device);

                // Apply volume limit immediately to the sink
                if (request.MaxVolume.HasValue)
                {
                    await backendFactory.SetVolumeAsync(device.Id, request.MaxVolume.Value, ct);
                }

                logger.LogInformation("Set max volume for device {DeviceId}: {MaxVolume}%", id, request.MaxVolume);

                return Results.Ok(new
                {
                    success = true,
                    deviceId = id,
                    deviceKey,
                    maxVolume = request.MaxVolume,
                    message = request.MaxVolume.HasValue
                        ? $"Max volume set to {request.MaxVolume}%"
                        : "Max volume limit cleared (using default 100%)"
                });
            }, logger, "set device max volume", id);
        })
        .WithName("SetDeviceMaxVolume")
        .WithDescription("Set the maximum volume limit for a device (applied to PulseAudio sink)");
    }
}

/// <summary>
/// Request to set a device alias.
/// </summary>
public record DeviceAliasRequest(string? Alias);

/// <summary>
/// Request to set device hidden status.
/// </summary>
public record DeviceHiddenRequest(bool Hidden);

/// <summary>
/// Request to set device maximum volume limit.
/// </summary>
/// <param name="MaxVolume">Maximum volume limit (0-100), or null to clear the limit.</param>
public record DeviceMaxVolumeRequest(
    [property: Range(0, 100, ErrorMessage = "MaxVolume must be between 0 and 100.")]
    int? MaxVolume);
