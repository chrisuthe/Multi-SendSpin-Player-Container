using MultiRoomAudio.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services;

/// <summary>
/// Persisted device configuration with alias and stable identifiers.
/// Used for re-matching devices when PulseAudio sink names change across reboots.
/// </summary>
public class DeviceConfiguration
{
    /// <summary>
    /// User-defined friendly name for the device (e.g., "Kitchen Speaker").
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// Last known PulseAudio sink name (may become stale after reboot).
    /// </summary>
    public string? LastKnownSinkName { get; set; }

    /// <summary>
    /// Stable identifiers for re-matching devices across reboots.
    /// </summary>
    public DeviceIdentifiersConfig? Identifiers { get; set; }

    /// <summary>
    /// When this device was first seen.
    /// </summary>
    public DateTime? FirstSeen { get; set; }

    /// <summary>
    /// When this device was last matched/seen.
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Whether this device is hidden from player creation.
    /// Hidden devices don't appear in dropdowns by default (useful for HDMI outputs).
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Maximum volume limit for this device (0-100%).
    /// Applied to the PulseAudio sink at startup and when changed via API.
    /// </summary>
    public int? MaxVolume { get; set; }
}

/// <summary>
/// YAML-serializable version of DeviceIdentifiers.
/// </summary>
public class DeviceIdentifiersConfig
{
    public string? Serial { get; set; }
    public string? BusPath { get; set; }
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? AlsaLongCardName { get; set; }

    /// <summary>
    /// Create from the model record.
    /// </summary>
    public static DeviceIdentifiersConfig? FromModel(DeviceIdentifiers? identifiers)
    {
        if (identifiers == null)
            return null;
        return new DeviceIdentifiersConfig
        {
            Serial = identifiers.Serial,
            BusPath = identifiers.BusPath,
            VendorId = identifiers.VendorId,
            ProductId = identifiers.ProductId,
            AlsaLongCardName = identifiers.AlsaLongCardName
        };
    }

    /// <summary>
    /// Convert to the model record.
    /// </summary>
    public DeviceIdentifiers ToModel() => new(Serial, BusPath, VendorId, ProductId, AlsaLongCardName);
}

/// <summary>
/// Configuration for a single player.
/// Matches the YAML format from the Python implementation for backward compatibility.
/// </summary>
public class PlayerConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string Provider { get; set; } = "sendspin";
    public bool Autostart { get; set; } = true;
    public int DelayMs { get; set; } = 0;
    public string? Server { get; set; }
    public int? Volume { get; set; }

    // PortAudio device index (for Sendspin SDK)
    public int? PortAudioDeviceIndex { get; set; }

    // Additional provider-specific settings
    public Dictionary<string, object>? Extra { get; set; }
}

/// <summary>
/// Manages player configuration persistence with YAML storage.
/// Provides a clean interface for configuration operations.
/// </summary>
public class ConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly EnvironmentService _environment;
    private readonly string _playersConfigPath;
    private readonly string _devicesConfigPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly object _lock = new();

    private Dictionary<string, PlayerConfiguration> _players = new();
    private Dictionary<string, DeviceConfiguration> _devices = new();

    public ConfigurationService(
        ILogger<ConfigurationService> logger,
        EnvironmentService environment)
    {
        _logger = logger;
        _environment = environment;
        _playersConfigPath = environment.PlayersConfigPath;
        _devicesConfigPath = environment.DevicesConfigPath;

        _logger.LogDebug("Initializing ConfigurationService with players config: {PlayersPath}, devices config: {DevicesPath}",
            _playersConfigPath, _devicesConfigPath);

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        _environment.EnsureDirectoriesExist();
        Load();
        LoadDevices();
    }

    /// <summary>
    /// All configured players.
    /// </summary>
    public IReadOnlyDictionary<string, PlayerConfiguration> Players => _players;

    /// <summary>
    /// All configured devices (with aliases and stable identifiers).
    /// </summary>
    public IReadOnlyDictionary<string, DeviceConfiguration> Devices => _devices;

    /// <summary>
    /// Check if any players are configured.
    /// </summary>
    public bool HasPlayers => _players.Count > 0;

    /// <summary>
    /// Load player configurations from YAML file.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            _logger.LogDebug("Loading player configuration from {ConfigPath}", _playersConfigPath);

            if (!File.Exists(_playersConfigPath))
            {
                _logger.LogInformation("Config file {ConfigPath} does not exist, starting fresh", _playersConfigPath);
                _players = new Dictionary<string, PlayerConfiguration>();
                return;
            }

            try
            {
                var yaml = File.ReadAllText(_playersConfigPath);

                // Handle empty or whitespace-only YAML files
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    _logger.LogInformation("Config file {ConfigPath} is empty, starting fresh", _playersConfigPath);
                    _players = new Dictionary<string, PlayerConfiguration>();
                    return;
                }

                var raw = _deserializer.Deserialize<Dictionary<string, PlayerConfiguration>>(yaml);
                _players = raw ?? new Dictionary<string, PlayerConfiguration>();

                // Ensure name field matches dictionary key
                foreach (var (name, config) in _players)
                {
                    config.Name = name;
                }

                _logger.LogInformation("Loaded {PlayerCount} players from configuration", _players.Count);

                // Log player names at debug level for troubleshooting
                if (_players.Count > 0)
                {
                    _logger.LogDebug("Configured players: {PlayerNames}",
                        string.Join(", ", _players.Keys));
                }
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                _logger.LogError(ex,
                    "Failed to parse YAML configuration from {ConfigPath}. File may be malformed",
                    _playersConfigPath);
                _players = new Dictionary<string, PlayerConfiguration>();
            }
            catch (IOException ex)
            {
                _logger.LogError(ex,
                    "Failed to read configuration file {ConfigPath}. Check file permissions",
                    _playersConfigPath);
                _players = new Dictionary<string, PlayerConfiguration>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error loading config from {ConfigPath}", _playersConfigPath);
                _players = new Dictionary<string, PlayerConfiguration>();
            }
        }
    }

    /// <summary>
    /// Save current player configurations to YAML file.
    /// </summary>
    public bool Save()
    {
        lock (_lock)
        {
            try
            {
                _logger.LogDebug("Saving {PlayerCount} players to configuration", _players.Count);
                var yaml = _serializer.Serialize(_players);
                File.WriteAllText(_playersConfigPath, yaml);
                _logger.LogInformation("Configuration saved successfully ({PlayerCount} players)",
                    _players.Count);
                return true;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex,
                    "Failed to write configuration file {ConfigPath}. Check disk space and permissions",
                    _playersConfigPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saving config to {ConfigPath}", _playersConfigPath);
                return false;
            }
        }
    }

    /// <summary>
    /// Get a player configuration by name.
    /// </summary>
    public PlayerConfiguration? GetPlayer(string name)
    {
        lock (_lock)
        {
            return _players.TryGetValue(name, out var config) ? config : null;
        }
    }

    /// <summary>
    /// Add or update a player configuration.
    /// </summary>
    public void SetPlayer(string name, PlayerConfiguration config)
    {
        lock (_lock)
        {
            var isUpdate = _players.ContainsKey(name);
            config.Name = name;
            _players[name] = config;

            if (isUpdate)
            {
                _logger.LogDebug("Updated player configuration: {PlayerName}", name);
            }
            else
            {
                _logger.LogInformation("Added player configuration: {PlayerName}", name);
            }

            _logger.LogDebug(
                "Player {PlayerName} config: Device={Device}, Autostart={Autostart}, Server={Server}",
                name, config.Device ?? "(default)", config.Autostart, config.Server ?? "(auto-discover)");
        }
    }

    /// <summary>
    /// Delete a player configuration.
    /// </summary>
    public bool DeletePlayer(string name)
    {
        lock (_lock)
        {
            if (_players.Remove(name))
            {
                _logger.LogInformation("Deleted player configuration: {PlayerName}", name);
                return true;
            }

            _logger.LogDebug("Attempted to delete non-existent player: {PlayerName}", name);
            return false;
        }
    }

    /// <summary>
    /// Check if a player exists.
    /// </summary>
    public bool PlayerExists(string name)
    {
        lock (_lock)
        {
            return _players.ContainsKey(name);
        }
    }

    /// <summary>
    /// Get list of all player names.
    /// </summary>
    public IReadOnlyList<string> ListPlayers()
    {
        lock (_lock)
        {
            return _players.Keys.ToList();
        }
    }

    /// <summary>
    /// Update a single field in a player's configuration and optionally save.
    /// </summary>
    public bool UpdatePlayerField(string name, Action<PlayerConfiguration> update, bool save = true)
    {
        lock (_lock)
        {
            if (!_players.TryGetValue(name, out var config))
                return false;

            update(config);
            _logger.LogDebug("Updated player config field: {Name}", name);

            if (save)
                Save();

            return true;
        }
    }

    /// <summary>
    /// Get all players configured for autostart.
    /// </summary>
    public IReadOnlyList<PlayerConfiguration> GetAutostartPlayers()
    {
        lock (_lock)
        {
            return _players.Values.Where(p => p.Autostart).ToList();
        }
    }

    /// <summary>
    /// Rename a player (change its key in the config).
    /// </summary>
    public bool RenamePlayer(string oldName, string newName)
    {
        lock (_lock)
        {
            if (!_players.TryGetValue(oldName, out var config))
                return false;

            if (_players.ContainsKey(newName) && oldName != newName)
                return false;

            _players.Remove(oldName);
            config.Name = newName;
            _players[newName] = config;

            _logger.LogDebug("Renamed player: {OldName} -> {NewName}", oldName, newName);
            return true;
        }
    }

    // =========================================================================
    // Device Configuration Management
    // =========================================================================

    /// <summary>
    /// Load device configurations from YAML file.
    /// </summary>
    public void LoadDevices()
    {
        lock (_lock)
        {
            _logger.LogDebug("Loading device configuration from {ConfigPath}", _devicesConfigPath);

            if (!File.Exists(_devicesConfigPath))
            {
                _logger.LogDebug("Devices config file does not exist, starting fresh");
                _devices = new Dictionary<string, DeviceConfiguration>();
                return;
            }

            try
            {
                var yaml = File.ReadAllText(_devicesConfigPath);

                if (string.IsNullOrWhiteSpace(yaml))
                {
                    _logger.LogDebug("Devices config file is empty, starting fresh");
                    _devices = new Dictionary<string, DeviceConfiguration>();
                    return;
                }

                var raw = _deserializer.Deserialize<Dictionary<string, DeviceConfiguration>>(yaml);
                _devices = raw ?? new Dictionary<string, DeviceConfiguration>();

                _logger.LogInformation("Loaded {DeviceCount} device configurations", _devices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load device configuration from {ConfigPath}", _devicesConfigPath);
                _devices = new Dictionary<string, DeviceConfiguration>();
            }
        }
    }

    /// <summary>
    /// Save device configurations to YAML file.
    /// </summary>
    public bool SaveDevices()
    {
        lock (_lock)
        {
            try
            {
                _logger.LogDebug("Saving {DeviceCount} devices to configuration", _devices.Count);
                var yaml = _serializer.Serialize(_devices);
                File.WriteAllText(_devicesConfigPath, yaml);
                _logger.LogDebug("Device configuration saved successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save device configuration to {ConfigPath}", _devicesConfigPath);
                return false;
            }
        }
    }

    /// <summary>
    /// Get a device configuration by its key (generated from stable identifiers).
    /// </summary>
    public DeviceConfiguration? GetDevice(string deviceKey)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(deviceKey, out var config) ? config : null;
        }
    }

    /// <summary>
    /// Set or update a device configuration.
    /// </summary>
    public void SetDevice(string deviceKey, DeviceConfiguration config)
    {
        lock (_lock)
        {
            _devices[deviceKey] = config;
            _logger.LogDebug("Set device configuration: {DeviceKey}, Alias={Alias}",
                deviceKey, config.Alias ?? "(none)");
        }
    }

    /// <summary>
    /// Get the alias for a device by its current sink name.
    /// Searches through all device configurations to find a match.
    /// </summary>
    public string? GetDeviceAliasBySinkName(string sinkName)
    {
        lock (_lock)
        {
            var device = _devices.Values.FirstOrDefault(d =>
                d.LastKnownSinkName?.Equals(sinkName, StringComparison.OrdinalIgnoreCase) == true);
            return device?.Alias;
        }
    }

    /// <summary>
    /// Get the device configuration by its current sink name.
    /// </summary>
    public DeviceConfiguration? GetDeviceConfigBySinkName(string sinkName)
    {
        lock (_lock)
        {
            return _devices.Values.FirstOrDefault(d =>
                d.LastKnownSinkName?.Equals(sinkName, StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    /// <summary>
    /// Set the hidden status for a device.
    /// </summary>
    public bool SetDeviceHidden(string deviceKey, bool hidden, AudioDevice? currentDevice = null)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceKey, out var config))
            {
                config = new DeviceConfiguration
                {
                    FirstSeen = DateTime.UtcNow
                };
                _devices[deviceKey] = config;
            }

            config.Hidden = hidden;
            config.LastSeen = DateTime.UtcNow;

            // Update from current device info if provided
            if (currentDevice != null)
            {
                config.LastKnownSinkName = currentDevice.Id;
                config.Identifiers = DeviceIdentifiersConfig.FromModel(currentDevice.Identifiers);
            }

            _logger.LogInformation("Set device hidden: {DeviceKey} = {Hidden}", deviceKey, hidden);
            return SaveDevices();
        }
    }

    /// <summary>
    /// Set the maximum volume limit for a device.
    /// </summary>
    public bool SetDeviceMaxVolume(string deviceKey, int? maxVolume, AudioDevice? currentDevice = null)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceKey, out var config))
            {
                config = new DeviceConfiguration
                {
                    FirstSeen = DateTime.UtcNow
                };
                _devices[deviceKey] = config;
            }

            config.MaxVolume = maxVolume;
            config.LastSeen = DateTime.UtcNow;

            // Update from current device info if provided
            if (currentDevice != null)
            {
                config.LastKnownSinkName = currentDevice.Id;
                config.Identifiers = DeviceIdentifiersConfig.FromModel(currentDevice.Identifiers);
            }

            _logger.LogInformation("Set device max volume: {DeviceKey} = {MaxVolume}%",
                deviceKey, maxVolume?.ToString() ?? "(cleared)");
            return SaveDevices();
        }
    }

    /// <summary>
    /// Set the alias for a device, creating or updating its configuration.
    /// </summary>
    public bool SetDeviceAlias(string deviceKey, string? alias, AudioDevice? currentDevice = null)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceKey, out var config))
            {
                config = new DeviceConfiguration
                {
                    FirstSeen = DateTime.UtcNow
                };
                _devices[deviceKey] = config;
            }

            config.Alias = alias;
            config.LastSeen = DateTime.UtcNow;

            // Update from current device info if provided
            if (currentDevice != null)
            {
                config.LastKnownSinkName = currentDevice.Id;
                config.Identifiers = DeviceIdentifiersConfig.FromModel(currentDevice.Identifiers);
            }

            _logger.LogInformation("Set device alias: {DeviceKey} = '{Alias}'", deviceKey, alias ?? "(cleared)");
            return SaveDevices();
        }
    }

    /// <summary>
    /// Generate a stable device key from device identifiers.
    /// Priority: Serial > BusPath > VendorId+ProductId
    /// </summary>
    public static string GenerateDeviceKey(AudioDevice device)
    {
        var id = device.Identifiers;

        // Use serial number if available (most stable)
        if (!string.IsNullOrEmpty(id?.Serial))
        {
            return $"serial_{SanitizeKey(id.Serial)}";
        }

        // Use bus path if available (stable per USB port)
        if (!string.IsNullOrEmpty(id?.BusPath))
        {
            return $"path_{SanitizeKey(id.BusPath)}";
        }

        // Use vendor+product ID combination
        if (!string.IsNullOrEmpty(id?.VendorId) && !string.IsNullOrEmpty(id?.ProductId))
        {
            return $"usb_{id.VendorId}_{id.ProductId}";
        }

        // Fallback to sink name (least stable)
        return $"sink_{SanitizeKey(device.Id)}";
    }

    /// <summary>
    /// Sanitize a string for use as a YAML key.
    /// </summary>
    private static string SanitizeKey(string input)
    {
        // Replace problematic characters with underscores
        return string.Join("_", input.Split(Path.GetInvalidFileNameChars()))
            .Replace(":", "_")
            .Replace(".", "_")
            .Replace("-", "_")
            .ToLowerInvariant();
    }

    /// <summary>
    /// Update device configuration with current device info (for re-matching tracking).
    /// </summary>
    public void UpdateDeviceInfo(string deviceKey, AudioDevice device)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceKey, out var config))
            {
                config = new DeviceConfiguration
                {
                    FirstSeen = DateTime.UtcNow
                };
                _devices[deviceKey] = config;
            }

            config.LastKnownSinkName = device.Id;
            config.LastSeen = DateTime.UtcNow;
            config.Identifiers = DeviceIdentifiersConfig.FromModel(device.Identifiers);
        }
    }

    /// <summary>
    /// Get all device aliases as a dictionary of sink name to alias.
    /// </summary>
    public Dictionary<string, string> GetAllDeviceAliases()
    {
        lock (_lock)
        {
            return _devices
                .Where(d => !string.IsNullOrEmpty(d.Value.Alias) && !string.IsNullOrEmpty(d.Value.LastKnownSinkName))
                .ToDictionary(d => d.Value.LastKnownSinkName!, d => d.Value.Alias!);
        }
    }

    /// <summary>
    /// Get all device configurations (for applying startup settings like volume limits).
    /// </summary>
    public IReadOnlyDictionary<string, DeviceConfiguration> GetAllDeviceConfigurations()
    {
        lock (_lock)
        {
            return new Dictionary<string, DeviceConfiguration>(_devices);
        }
    }
}
