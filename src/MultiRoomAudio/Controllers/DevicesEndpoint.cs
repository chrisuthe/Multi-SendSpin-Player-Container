using MultiRoomAudio.Audio;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for audio device enumeration.
/// </summary>
public static class DevicesEndpoint
{
    public static void MapDevicesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/devices")
            .WithTags("Devices")
            .WithOpenApi();

        // GET /api/devices - List all output devices
        group.MapGet("/", (ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: GET /api/devices - Enumerating audio devices");
            try
            {
                var devices = PortAudioDeviceEnumerator.GetOutputDevices().ToList();
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
                    defaultDevice = devices.FirstOrDefault(d => d.IsDefault)?.Id
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to enumerate audio devices. PortAudio may not be initialized");
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to enumerate devices");
            }
        })
        .WithName("ListDevices")
        .WithDescription("List all available audio output devices");

        // GET /api/devices/{id} - Get specific device
        group.MapGet("/{id}", (string id, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: GET /api/devices/{DeviceId}", id);
            try
            {
                var device = PortAudioDeviceEnumerator.GetDevice(id);
                if (device == null)
                {
                    logger.LogDebug("Device {DeviceId} not found", id);
                    return Results.NotFound(new ErrorResponse(false, $"Device '{id}' not found"));
                }

                return Results.Ok(device);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get device info for {DeviceId}", id);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to get device info");
            }
        })
        .WithName("GetDevice")
        .WithDescription("Get details of a specific audio device");

        // GET /api/devices/default - Get default device
        group.MapGet("/default", (ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: GET /api/devices/default");
            try
            {
                var device = PortAudioDeviceEnumerator.GetDefaultDevice();
                if (device == null)
                {
                    logger.LogWarning("No default audio output device found");
                    return Results.NotFound(new ErrorResponse(false, "No default output device found"));
                }

                logger.LogDebug("Default device: {DeviceName} (index {DeviceIndex})",
                    device.Name, device.Index);
                return Results.Ok(device);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get default audio device");
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to get default device");
            }
        })
        .WithName("GetDefaultDevice")
        .WithDescription("Get the default audio output device");

        // POST /api/devices/refresh - Refresh device list
        group.MapPost("/refresh", (ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DevicesEndpoint");
            logger.LogDebug("API: POST /api/devices/refresh");
            try
            {
                logger.LogInformation("Refreshing audio device list...");
                PortAudioDeviceEnumerator.RefreshDevices();
                var devices = PortAudioDeviceEnumerator.GetOutputDevices().ToList();

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
                    count = devices.Count
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh audio devices");
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to refresh devices");
            }
        })
        .WithName("RefreshDevices")
        .WithDescription("Re-enumerate audio devices (detect newly connected USB devices)");
    }
}
