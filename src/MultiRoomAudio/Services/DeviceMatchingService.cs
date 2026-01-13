using MultiRoomAudio.Audio;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Services;

/// <summary>
/// Service for matching persisted device configurations with current PulseAudio sinks.
/// Handles the case where sink names change but we can identify devices by stable properties.
/// </summary>
public class DeviceMatchingService
{
    private readonly ILogger<DeviceMatchingService> _logger;
    private readonly ConfigurationService _config;
    private readonly BackendFactory _backend;
    private readonly CustomSinksService _customSinks;

    public DeviceMatchingService(
        ILogger<DeviceMatchingService> logger,
        ConfigurationService config,
        BackendFactory backend,
        CustomSinksService customSinks)
    {
        _logger = logger;
        _config = config;
        _backend = backend;
        _customSinks = customSinks;
    }

    /// <summary>
    /// Result of a device match attempt.
    /// </summary>
    public record DeviceMatchResult(
        string DeviceKey,
        string? PersistedSinkName,
        string? CurrentSinkName,
        string? MatchMethod,  // "serial", "bus_path", "vendor_product", "sink_name", or null
        bool WasUpdated
    );

    /// <summary>
    /// Attempts to find the current sink name for a persisted device configuration.
    /// Uses a priority-based matching strategy.
    /// </summary>
    public string? FindCurrentSinkName(DeviceConfiguration persistedDevice)
    {
        var devices = _backend.GetOutputDevices().ToList();
        var identifiers = persistedDevice.Identifiers;

        if (identifiers == null && string.IsNullOrEmpty(persistedDevice.LastKnownSinkName))
        {
            _logger.LogDebug("No identifiers or sink name for device, cannot match");
            return null;
        }

        // Priority 1: Serial number (most reliable)
        if (!string.IsNullOrEmpty(identifiers?.Serial))
        {
            var match = devices.FirstOrDefault(d =>
                d.Identifiers?.Serial?.Equals(identifiers.Serial, StringComparison.OrdinalIgnoreCase) == true);
            if (match != null)
            {
                _logger.LogDebug("Matched device by serial: {Serial} -> {SinkName}", identifiers.Serial, match.Id);
                return match.Id;
            }
        }

        // Priority 2: Bus path (stable per USB port)
        if (!string.IsNullOrEmpty(identifiers?.BusPath))
        {
            var match = devices.FirstOrDefault(d =>
                d.Identifiers?.BusPath?.Equals(identifiers.BusPath, StringComparison.OrdinalIgnoreCase) == true);
            if (match != null)
            {
                _logger.LogDebug("Matched device by bus path: {BusPath} -> {SinkName}", identifiers.BusPath, match.Id);
                return match.Id;
            }
        }

        // Priority 3: Vendor+Product ID with partial name match
        if (!string.IsNullOrEmpty(identifiers?.VendorId) && !string.IsNullOrEmpty(identifiers?.ProductId))
        {
            var matches = devices.Where(d =>
                d.Identifiers?.VendorId?.Equals(identifiers.VendorId, StringComparison.OrdinalIgnoreCase) == true &&
                d.Identifiers?.ProductId?.Equals(identifiers.ProductId, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            if (matches.Count == 1)
            {
                _logger.LogDebug("Matched device by vendor+product ID: {VendorId}:{ProductId} -> {SinkName}",
                    identifiers.VendorId, identifiers.ProductId, matches[0].Id);
                return matches[0].Id;
            }

            // Multiple matches with same vendor+product - try ALSA long card name
            if (matches.Count > 1 && !string.IsNullOrEmpty(identifiers.AlsaLongCardName))
            {
                var exactMatch = matches.FirstOrDefault(d =>
                    d.Identifiers?.AlsaLongCardName?.Equals(identifiers.AlsaLongCardName, StringComparison.OrdinalIgnoreCase) == true);
                if (exactMatch != null)
                {
                    _logger.LogDebug("Matched device by vendor+product+ALSA name: {Name} -> {SinkName}",
                        identifiers.AlsaLongCardName, exactMatch.Id);
                    return exactMatch.Id;
                }
            }
        }

        // Priority 4: Try exact sink name match (might still work)
        if (!string.IsNullOrEmpty(persistedDevice.LastKnownSinkName))
        {
            var match = devices.FirstOrDefault(d =>
                d.Id.Equals(persistedDevice.LastKnownSinkName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                _logger.LogDebug("Matched device by sink name: {SinkName}", match.Id);
                return match.Id;
            }
        }

        _logger.LogDebug("Could not match device with identifiers: Serial={Serial}, BusPath={BusPath}, Vendor={Vendor}:{Product}",
            identifiers?.Serial ?? "(none)",
            identifiers?.BusPath ?? "(none)",
            identifiers?.VendorId ?? "(none)",
            identifiers?.ProductId ?? "(none)");
        return null;
    }

    /// <summary>
    /// Determines the match method used for a successful match.
    /// </summary>
    private string? DetermineMatchMethod(DeviceConfiguration persisted, AudioDevice current)
    {
        var pId = persisted.Identifiers;
        var cId = current.Identifiers;

        if (!string.IsNullOrEmpty(pId?.Serial) &&
            pId.Serial.Equals(cId?.Serial, StringComparison.OrdinalIgnoreCase))
            return "serial";

        if (!string.IsNullOrEmpty(pId?.BusPath) &&
            pId.BusPath.Equals(cId?.BusPath, StringComparison.OrdinalIgnoreCase))
            return "bus_path";

        if (!string.IsNullOrEmpty(pId?.VendorId) && !string.IsNullOrEmpty(pId?.ProductId) &&
            pId.VendorId.Equals(cId?.VendorId, StringComparison.OrdinalIgnoreCase) &&
            pId.ProductId.Equals(cId?.ProductId, StringComparison.OrdinalIgnoreCase))
            return "vendor_product";

        if (!string.IsNullOrEmpty(persisted.LastKnownSinkName) &&
            persisted.LastKnownSinkName.Equals(current.Id, StringComparison.OrdinalIgnoreCase))
            return "sink_name";

        return null;
    }

    /// <summary>
    /// Matches all persisted devices to current sinks and returns a mapping.
    /// </summary>
    public List<DeviceMatchResult> MatchAllDevices()
    {
        var results = new List<DeviceMatchResult>();
        var devices = _backend.GetOutputDevices().ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var (deviceKey, persisted) in _config.Devices)
        {
            var currentSinkName = FindCurrentSinkName(persisted);
            var wasUpdated = false;
            string? matchMethod = null;

            if (currentSinkName != null && devices.TryGetValue(currentSinkName, out var currentDevice))
            {
                matchMethod = DetermineMatchMethod(persisted, currentDevice);

                // Update if sink name changed
                if (persisted.LastKnownSinkName != currentSinkName)
                {
                    _config.UpdateDeviceInfo(deviceKey, currentDevice);
                    wasUpdated = true;
                    _logger.LogInformation(
                        "Device re-matched: {DeviceKey} ({Alias}) sink changed from {OldSink} to {NewSink} via {Method}",
                        deviceKey,
                        persisted.Alias ?? "(no alias)",
                        persisted.LastKnownSinkName ?? "(unknown)",
                        currentSinkName,
                        matchMethod ?? "unknown");
                }
            }

            results.Add(new DeviceMatchResult(
                DeviceKey: deviceKey,
                PersistedSinkName: persisted.LastKnownSinkName,
                CurrentSinkName: currentSinkName,
                MatchMethod: matchMethod,
                WasUpdated: wasUpdated
            ));
        }

        return results;
    }

    /// <summary>
    /// Updates player configurations to use new device IDs after re-matching.
    /// Returns list of player names that were updated.
    /// </summary>
    public List<string> UpdatePlayerDevices()
    {
        var updatedPlayers = new List<string>();
        var matchResults = MatchAllDevices();

        // Build a map of old sink name -> new sink name
        var sinkNameChanges = matchResults
            .Where(r => r.WasUpdated && r.PersistedSinkName != null && r.CurrentSinkName != null)
            .ToDictionary(r => r.PersistedSinkName!, r => r.CurrentSinkName!, StringComparer.OrdinalIgnoreCase);

        if (sinkNameChanges.Count == 0)
        {
            _logger.LogDebug("No device sink name changes detected");
            return updatedPlayers;
        }

        // Update player configurations
        foreach (var player in _config.Players.Values)
        {
            if (!string.IsNullOrEmpty(player.Device) && sinkNameChanges.TryGetValue(player.Device, out var newSinkName))
            {
                _logger.LogInformation(
                    "Updating player {PlayerName} device from {OldDevice} to {NewDevice}",
                    player.Name, player.Device, newSinkName);

                _config.UpdatePlayerField(player.Name, p => p.Device = newSinkName, save: false);
                updatedPlayers.Add(player.Name);
            }
        }

        if (updatedPlayers.Count > 0)
        {
            _config.Save();
            _config.SaveDevices();
        }

        return updatedPlayers;
    }

    /// <summary>
    /// Enrich an AudioDevice with its alias, hidden status, and custom sink name.
    /// </summary>
    public AudioDevice EnrichWithConfig(AudioDevice device)
    {
        var enriched = device;

        // Check if this device is a custom sink and use its name as display name
        var customSink = _customSinks.GetSink(device.Id);
        if (customSink != null)
        {
            // Use the custom sink's user-provided name instead of PulseAudio's auto-generated description
            enriched = enriched with { Name = customSink.Name };
        }

        // Apply device config (alias, hidden status) if present
        var config = _config.GetDeviceConfigBySinkName(device.Id);
        if (config != null)
        {
            enriched = enriched with
            {
                Alias = config.Alias,
                Hidden = config.Hidden
            };
        }

        return enriched;
    }

    /// <summary>
    /// Get all output devices enriched with their aliases and hidden status.
    /// </summary>
    public IEnumerable<AudioDevice> GetEnrichedDevices()
    {
        return _backend.GetOutputDevices().Select(EnrichWithConfig);
    }
}
