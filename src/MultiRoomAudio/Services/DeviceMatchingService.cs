using MultiRoomAudio.Audio;
using MultiRoomAudio.Audio.PulseAudio;
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
    private readonly AlsaCapabilityService _alsaCapabilities;

    public DeviceMatchingService(
        ILogger<DeviceMatchingService> logger,
        ConfigurationService config,
        BackendFactory backend,
        CustomSinksService customSinks,
        AlsaCapabilityService alsaCapabilities)
    {
        _logger = logger;
        _config = config;
        _backend = backend;
        _customSinks = customSinks;
        _alsaCapabilities = alsaCapabilities;
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
    /// Enrich an AudioDevice with its alias, hidden status, custom sink name, and capabilities.
    /// </summary>
    public AudioDevice EnrichWithConfig(AudioDevice device)
    {
        var enriched = device;

        // Check if this device is a custom sink and use its name and type
        var customSink = _customSinks.GetSink(device.Id);
        if (customSink != null)
        {
            // Use the custom sink's user-provided name and mark its type
            enriched = enriched with
            {
                Name = customSink.Name,
                SinkType = customSink.Type.ToString()
            };
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

        // Enrich with ALSA capabilities if we have an ALSA card index
        if (device.CardIndex.HasValue && device.Capabilities == null)
        {
            var capsWithSource = _alsaCapabilities.GetCapabilities(
                device.CardIndex.Value,
                device.DefaultSampleRate,
                device.BitDepth,
                device.MaxChannels);

            if (capsWithSource != null)
            {
                enriched = enriched with
                {
                    Capabilities = capsWithSource.Capabilities,
                    CapabilitySource = capsWithSource.Source
                };
            }
        }

        return enriched;
    }

    /// <summary>
    /// Get a single device by ID, enriched with alias and hidden status.
    /// Returns null if the device is not found.
    /// </summary>
    public AudioDevice? GetEnrichedDevice(string deviceId)
    {
        var device = _backend.GetDevice(deviceId);
        if (device != null)
        {
            return EnrichWithConfig(device);
        }

        // Check if it's a custom sink
        var customSink = _customSinks.GetSink(deviceId);
        if (customSink != null)
        {
            var customDevice = new AudioDevice(
                Index: 1000,
                Id: customSink.Name,
                Name: customSink.Description ?? customSink.Name,
                MaxChannels: customSink.Channels ?? 2,
                DefaultSampleRate: 48000,
                DefaultLowLatencyMs: 20,
                DefaultHighLatencyMs: 100,
                IsDefault: false,
                Capabilities: new DeviceCapabilities(
                    SupportedSampleRates: new[] { 44100, 48000, 96000, 192000 },
                    SupportedBitDepths: new[] { 16, 24, 32 },
                    MaxChannels: customSink.Channels ?? 2,
                    PreferredSampleRate: 48000,
                    PreferredBitDepth: 24
                ),
                Identifiers: new DeviceIdentifiers(
                    Serial: null,
                    BusPath: null,
                    VendorId: null,
                    ProductId: null,
                    AlsaLongCardName: $"Custom {customSink.Type} Sink",
                    BluetoothMac: null,
                    BluetoothCodec: null
                )
            );
            return EnrichWithConfig(customDevice);
        }

        return null;
    }

    /// <summary>
    /// Get all output devices enriched with their aliases and hidden status.
    /// Includes both hardware devices and custom sinks.
    /// Custom sinks take precedence over raw hardware devices with the same ID to avoid duplicates.
    /// Also tracks all discovered hardware devices for persistence.
    /// </summary>
    public IEnumerable<AudioDevice> GetEnrichedDevices()
    {
        // Get custom sinks first to build exclusion set
        var customSinksResponse = _customSinks.GetAllSinks();
        var loadedCustomSinks = customSinksResponse.Sinks
            .Where(sink => sink.State == CustomSinkState.Loaded && !string.IsNullOrEmpty(sink.PulseAudioSinkName))
            .ToList();

        // Build set of PulseAudio sink names that belong to custom sinks
        var customSinkIds = loadedCustomSinks
            .Select(sink => sink.PulseAudioSinkName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Get hardware devices, excluding any that are custom sinks (to avoid duplicates)
        var rawHardwareDevices = _backend.GetOutputDevices()
            .Where(device => !customSinkIds.Contains(device.Id))
            .ToList();

        // Track all hardware devices with identifiers for persistence
        // Only save if new devices were discovered (not on every API call)
        var newDevicesFound = false;
        foreach (var device in rawHardwareDevices)
        {
            if (device.Identifiers != null)
            {
                var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                if (_config.EnsureDeviceTracked(deviceKey, device))
                {
                    newDevicesFound = true;
                }
            }
        }

        // Save only when new devices are discovered
        if (newDevicesFound)
        {
            _config.SaveDevices();
        }

        // Enrich hardware devices with config
        var hardwareDevices = rawHardwareDevices.Select(EnrichWithConfig);

        // Convert custom sinks to AudioDevice format
        var customSinkDevices = loadedCustomSinks
            .Select(sink => new AudioDevice(
                Index: -1,  // Custom sinks don't have an index
                Id: sink.PulseAudioSinkName!,
                Name: sink.Name,
                MaxChannels: sink.Channels ?? 2,  // Default to stereo if not specified
                DefaultSampleRate: 48000,  // Standard sample rate for custom sinks
                DefaultLowLatencyMs: 0,
                DefaultHighLatencyMs: 0,
                IsDefault: false,
                Capabilities: null,
                Identifiers: null,
                Alias: sink.Description,  // Use description as alias (null if not set)
                Hidden: false,
                SinkType: sink.Type.ToString()  // "Combine" or "Remap"
            ));

        // Get cards with "off" profile that have available output profiles
        var offProfileDevices = GetOffProfileDevices(rawHardwareDevices);

        // Combine and return: hardware devices first, then off-profile devices, then custom sinks
        return hardwareDevices.Concat(offProfileDevices).Concat(customSinkDevices);
    }

    /// <summary>
    /// Gets cards with "off" profile that have available output profiles.
    /// These are potential devices that will work when the profile is activated.
    /// </summary>
    private IEnumerable<AudioDevice> GetOffProfileDevices(IReadOnlyCollection<AudioDevice> activeDevices)
    {
        var cards = PulseAudioCardEnumerator.GetCards().ToList();

        _logger.LogDebug("GetOffProfileDevices: Found {Count} cards total", cards.Count);

        // Build a set of card identifiers that already have active sinks
        var activeCardIdentifiers = activeDevices
            .Select(d => ExtractCardIdentifier(d.Id))
            .Where(id => id != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var card in cards)
        {
            _logger.LogDebug("GetOffProfileDevices: Checking card '{Card}' (profile: {Profile})",
                card.Name, card.ActiveProfile);

            // Skip if not "off" profile
            if (!card.ActiveProfile.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("GetOffProfileDevices: Card '{Card}' skipped - profile is '{Profile}', not 'off'",
                    card.Name, card.ActiveProfile);
                continue;
            }

            // Skip if card already has an active sink (shouldn't happen, but be safe)
            var cardIdentifier = ExtractCardIdentifier(card.Name);
            if (cardIdentifier != null && activeCardIdentifiers.Contains(cardIdentifier))
            {
                _logger.LogDebug("GetOffProfileDevices: Card '{Card}' skipped - already has active sink",
                    card.Name);
                continue;
            }

            // Find the best output profile (highest priority with sinks > 0)
            // Note: Don't filter by IsAvailable - that flag means "hardware is connected right now"
            // (e.g., headphones plugged in). We want to show the device even when hardware isn't
            // connected, since that's the whole point of this feature.
            var profilesWithSinks = card.Profiles.Where(p => p.Sinks > 0).ToList();
            _logger.LogDebug("GetOffProfileDevices: Card '{Card}' has {Count} profiles with sinks > 0",
                card.Name, profilesWithSinks.Count);

            var bestProfile = profilesWithSinks
                .OrderByDescending(p => p.Priority)
                .FirstOrDefault();

            if (bestProfile == null)
            {
                // Expected for input-only cards - keep at Debug to avoid alert noise
                _logger.LogDebug("GetOffProfileDevices: Card '{Card}' has no output profiles (profiles with sinks > 0), skipping. " +
                    "Total profiles: {Total}, Profile names: [{Names}]",
                    card.Name, card.Profiles.Count,
                    string.Join(", ", card.Profiles.Select(p => $"{p.Name}(sinks={p.Sinks})")));
                continue;
            }

            // Predict what the sink name will be when profile is activated
            var predictedSinkName = PredictSinkName(card.Name, bestProfile.Name);
            if (predictedSinkName == null)
            {
                _logger.LogWarning("GetOffProfileDevices: Could not predict sink name for card '{Card}' profile '{Profile}'",
                    card.Name, bestProfile.Name);
                continue;
            }

            // Determine max channels from the best profile
            var maxChannels = GetChannelsFromProfile(bestProfile.Name);

            _logger.LogInformation("GetOffProfileDevices: Including off-profile card '{Card}' " +
                "(profile: {BestProfile}) with predicted sink '{Sink}'",
                card.Name, bestProfile.Name, predictedSinkName);

            yield return new AudioDevice(
                Index: -card.Index, // Negative index to distinguish from active devices
                Id: predictedSinkName,
                Name: $"{card.Description ?? card.Name} [off]",
                MaxChannels: maxChannels,
                DefaultSampleRate: 48000,
                DefaultLowLatencyMs: 50,
                DefaultHighLatencyMs: 200,
                IsDefault: false,
                Capabilities: null,
                Identifiers: card.Identifiers,
                Alias: null,
                Hidden: false,
                IsOffProfile: true,
                CardName: card.Name
            );
        }
    }

    /// <summary>
    /// Extracts the card identifier from a card name or sink name.
    /// e.g., "alsa_card.pci-0000_01_00.0" → "pci-0000_01_00.0"
    /// e.g., "alsa_output.pci-0000_01_00.0.analog-stereo" → "pci-0000_01_00.0"
    /// </summary>
    private static string? ExtractCardIdentifier(string name)
    {
        // Handle card names
        if (name.StartsWith("alsa_card.", StringComparison.OrdinalIgnoreCase))
            return name["alsa_card.".Length..];
        if (name.StartsWith("bluez_card.", StringComparison.OrdinalIgnoreCase))
            return name["bluez_card.".Length..];

        // Handle sink names - extract the middle part
        if (name.StartsWith("alsa_output.", StringComparison.OrdinalIgnoreCase))
        {
            var withoutPrefix = name["alsa_output.".Length..];
            // Find the last dot that separates identifier from profile
            var lastDotIndex = withoutPrefix.LastIndexOf('.');
            if (lastDotIndex > 0)
                return withoutPrefix[..lastDotIndex];
        }
        if (name.StartsWith("bluez_sink.", StringComparison.OrdinalIgnoreCase))
        {
            var withoutPrefix = name["bluez_sink.".Length..];
            var lastDotIndex = withoutPrefix.LastIndexOf('.');
            if (lastDotIndex > 0)
                return withoutPrefix[..lastDotIndex];
        }

        return null;
    }

    /// <summary>
    /// Predicts the sink name that will be created when a profile is activated.
    /// e.g., card "alsa_card.pci-0000_01_00.0" + profile "output:analog-stereo"
    ///       → "alsa_output.pci-0000_01_00.0.analog-stereo"
    /// Also handles combined profiles like "output:analog-stereo+input:analog-stereo"
    ///       → "alsa_output.pci-0000_01_00.0.analog-stereo" (output portion only)
    /// </summary>
    private static string? PredictSinkName(string cardName, string profileName)
    {
        // Extract card identifier
        string? identifier = null;
        string prefix;

        if (cardName.StartsWith("alsa_card.", StringComparison.OrdinalIgnoreCase))
        {
            identifier = cardName["alsa_card.".Length..];
            prefix = "alsa_output.";
        }
        else if (cardName.StartsWith("bluez_card.", StringComparison.OrdinalIgnoreCase))
        {
            identifier = cardName["bluez_card.".Length..];
            prefix = "bluez_sink.";
        }
        else
        {
            return null;
        }

        // Extract profile suffix for sink name
        // Profile formats:
        //   "output:analog-stereo" → "analog-stereo"
        //   "output:analog-stereo+input:analog-stereo" → "analog-stereo" (output portion only)
        //   "analog-stereo" → "analog-stereo" (no prefix)
        var profileSuffix = profileName;

        // Remove "output:" prefix if present
        if (profileSuffix.StartsWith("output:", StringComparison.OrdinalIgnoreCase))
            profileSuffix = profileSuffix["output:".Length..];

        // Handle combined profiles: strip "+input:..." portion
        // PulseAudio names sinks based only on the output profile part
        var plusIndex = profileSuffix.IndexOf("+input:", StringComparison.OrdinalIgnoreCase);
        if (plusIndex > 0)
            profileSuffix = profileSuffix[..plusIndex];

        return $"{prefix}{identifier}.{profileSuffix}";
    }

    /// <summary>
    /// Determines channel count from profile name.
    /// </summary>
    private static int GetChannelsFromProfile(string profileName)
    {
        var lower = profileName.ToLowerInvariant();
        return lower switch
        {
            var s when s.Contains("surround-71") => 8,
            var s when s.Contains("surround-51") => 6,
            var s when s.Contains("surround-40") => 4,
            var s when s.Contains("stereo") => 2,
            var s when s.Contains("mono") => 1,
            _ => 2 // Default to stereo
        };
    }
}
