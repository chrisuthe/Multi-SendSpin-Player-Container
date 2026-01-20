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
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

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
        _logger.LogDebug("Loading player configuration from {ConfigPath}", _playersConfigPath);

        // Read file content outside the lock to avoid blocking on slow I/O
        string? yaml = null;
        bool fileExists;

        try
        {
            fileExists = File.Exists(_playersConfigPath);
            if (fileExists)
            {
                yaml = File.ReadAllText(_playersConfigPath);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex,
                "Failed to read configuration file {ConfigPath}. Check file permissions",
                _playersConfigPath);
            _lock.EnterWriteLock();
            try
            {
                _players = new Dictionary<string, PlayerConfiguration>();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading config from {ConfigPath}", _playersConfigPath);
            _lock.EnterWriteLock();
            try
            {
                _players = new Dictionary<string, PlayerConfiguration>();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return;
        }

        // Process the data under write lock (since we're updating _players)
        _lock.EnterWriteLock();
        try
        {
            if (!fileExists)
            {
                _logger.LogInformation("Config file {ConfigPath} does not exist, starting fresh", _playersConfigPath);
                _players = new Dictionary<string, PlayerConfiguration>();
                return;
            }

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error parsing config from {ConfigPath}", _playersConfigPath);
            _players = new Dictionary<string, PlayerConfiguration>();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Save current player configurations to YAML file.
    /// </summary>
    public bool Save()
    {
        // Serialize under read lock (we're only reading _players)
        string yaml;
        int playerCount;
        _lock.EnterReadLock();
        try
        {
            _logger.LogDebug("Saving {PlayerCount} players to configuration", _players.Count);
            yaml = _serializer.Serialize(_players);
            playerCount = _players.Count;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Write file outside the lock
        try
        {
            File.WriteAllText(_playersConfigPath, yaml);
            _logger.LogInformation("Configuration saved successfully ({PlayerCount} players)",
                playerCount);
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

    /// <summary>
    /// Get a player configuration by name.
    /// </summary>
    public PlayerConfiguration? GetPlayer(string name)
    {
        _lock.EnterReadLock();
        try
        {
            return _players.TryGetValue(name, out var config) ? config : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Add or update a player configuration.
    /// </summary>
    public void SetPlayer(string name, PlayerConfiguration config)
    {
        _lock.EnterWriteLock();
        try
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
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Delete a player configuration.
    /// </summary>
    public bool DeletePlayer(string name)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_players.Remove(name))
            {
                _logger.LogInformation("Deleted player configuration: {PlayerName}", name);
                return true;
            }

            _logger.LogDebug("Attempted to delete non-existent player: {PlayerName}", name);
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Check if a player exists.
    /// </summary>
    public bool PlayerExists(string name)
    {
        _lock.EnterReadLock();
        try
        {
            return _players.ContainsKey(name);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get list of all player names.
    /// </summary>
    public IReadOnlyList<string> ListPlayers()
    {
        _lock.EnterReadLock();
        try
        {
            return _players.Keys.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Update a single field in a player's configuration and optionally save.
    /// </summary>
    public bool UpdatePlayerField(string name, Action<PlayerConfiguration> update, bool save = true)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_players.TryGetValue(name, out var config))
                return false;

            update(config);
            _logger.LogDebug("Updated player config field: {Name}", name);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // Save outside the lock
        if (save)
            Save();

        return true;
    }

    /// <summary>
    /// Get all players configured for autostart.
    /// </summary>
    public IReadOnlyList<PlayerConfiguration> GetAutostartPlayers()
    {
        _lock.EnterReadLock();
        try
        {
            return _players.Values.Where(p => p.Autostart).ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Rename a player (change its key in the config).
    /// </summary>
    public bool RenamePlayer(string oldName, string newName)
    {
        _lock.EnterWriteLock();
        try
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
        finally
        {
            _lock.ExitWriteLock();
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
        _logger.LogDebug("Loading device configuration from {ConfigPath}", _devicesConfigPath);

        // Read file content outside the lock
        string? yaml = null;
        bool fileExists;

        try
        {
            fileExists = File.Exists(_devicesConfigPath);
            if (fileExists)
            {
                yaml = File.ReadAllText(_devicesConfigPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read device configuration from {ConfigPath}", _devicesConfigPath);
            _lock.EnterWriteLock();
            try
            {
                _devices = new Dictionary<string, DeviceConfiguration>();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return;
        }

        // Process the data under write lock
        _lock.EnterWriteLock();
        try
        {
            if (!fileExists)
            {
                _logger.LogDebug("Devices config file does not exist, starting fresh");
                _devices = new Dictionary<string, DeviceConfiguration>();
                return;
            }

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
            _logger.LogError(ex, "Failed to parse device configuration from {ConfigPath}", _devicesConfigPath);
            _devices = new Dictionary<string, DeviceConfiguration>();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Save device configurations to YAML file.
    /// </summary>
    public bool SaveDevices()
    {
        // Serialize under read lock
        string yaml;
        int deviceCount;
        _lock.EnterReadLock();
        try
        {
            _logger.LogDebug("Saving {DeviceCount} devices to configuration", _devices.Count);
            yaml = _serializer.Serialize(_devices);
            deviceCount = _devices.Count;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Write file outside the lock
        try
        {
            File.WriteAllText(_devicesConfigPath, yaml);
            _logger.LogDebug("Device configuration saved successfully ({DeviceCount} devices)", deviceCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save device configuration to {ConfigPath}", _devicesConfigPath);
            return false;
        }
    }

    /// <summary>
    /// Get a device configuration by its key (generated from stable identifiers).
    /// </summary>
    public DeviceConfiguration? GetDevice(string deviceKey)
    {
        _lock.EnterReadLock();
        try
        {
            return _devices.TryGetValue(deviceKey, out var config) ? config : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Set or update a device configuration.
    /// </summary>
    public void SetDevice(string deviceKey, DeviceConfiguration config)
    {
        _lock.EnterWriteLock();
        try
        {
            _devices[deviceKey] = config;
            _logger.LogDebug("Set device configuration: {DeviceKey}, Alias={Alias}",
                deviceKey, config.Alias ?? "(none)");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Get the alias for a device by its current sink name.
    /// Searches through all device configurations to find a match.
    /// </summary>
    public string? GetDeviceAliasBySinkName(string sinkName)
    {
        _lock.EnterReadLock();
        try
        {
            var device = _devices.Values.FirstOrDefault(d =>
                d.LastKnownSinkName?.Equals(sinkName, StringComparison.OrdinalIgnoreCase) == true);
            return device?.Alias;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get the device configuration by its current sink name.
    /// </summary>
    public DeviceConfiguration? GetDeviceConfigBySinkName(string sinkName)
    {
        _lock.EnterReadLock();
        try
        {
            return _devices.Values.FirstOrDefault(d =>
                d.LastKnownSinkName?.Equals(sinkName, StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Generic helper method for updating a device property.
    /// Handles loading/creating the device config, updating the property, and saving.
    /// </summary>
    /// <typeparam name="T">The type of the property value.</typeparam>
    /// <param name="deviceKey">The device key to update.</param>
    /// <param name="propertyName">The name of the property (for logging).</param>
    /// <param name="value">The new value for the property.</param>
    /// <param name="setter">Action to set the property on the device configuration.</param>
    /// <param name="currentDevice">Optional current device info to update identifiers.</param>
    /// <param name="formatValue">Optional function to format the value for logging.</param>
    /// <returns>True if the configuration was saved successfully.</returns>
    private bool UpdateDeviceProperty<T>(
        string deviceKey,
        string propertyName,
        T value,
        Action<DeviceConfiguration, T> setter,
        AudioDevice? currentDevice = null,
        Func<T, string>? formatValue = null)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_devices.TryGetValue(deviceKey, out var config))
            {
                config = new DeviceConfiguration
                {
                    FirstSeen = DateTime.UtcNow
                };
                _devices[deviceKey] = config;
            }

            setter(config, value);
            config.LastSeen = DateTime.UtcNow;

            // Update from current device info if provided
            if (currentDevice != null)
            {
                config.LastKnownSinkName = currentDevice.Id;
                config.Identifiers = DeviceIdentifiersConfig.FromModel(currentDevice.Identifiers);
            }

            var formattedValue = formatValue != null ? formatValue(value) : value?.ToString() ?? "(null)";
            _logger.LogInformation("Set device {PropertyName}: {DeviceKey} = {Value}",
                propertyName, deviceKey, formattedValue);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // Save outside the lock
        return SaveDevices();
    }

    /// <summary>
    /// Set the hidden status for a device.
    /// </summary>
    public bool SetDeviceHidden(string deviceKey, bool hidden, AudioDevice? currentDevice = null)
    {
        return UpdateDeviceProperty(
            deviceKey,
            "hidden",
            hidden,
            (config, value) => config.Hidden = value,
            currentDevice);
    }

    /// <summary>
    /// Set the maximum volume limit for a device.
    /// </summary>
    public bool SetDeviceMaxVolume(string deviceKey, int? maxVolume, AudioDevice? currentDevice = null)
    {
        return UpdateDeviceProperty(
            deviceKey,
            "max volume",
            maxVolume,
            (config, value) => config.MaxVolume = value,
            currentDevice,
            v => v.HasValue ? $"{v}%" : "(cleared)");
    }

    /// <summary>
    /// Set the alias for a device, creating or updating its configuration.
    /// </summary>
    public bool SetDeviceAlias(string deviceKey, string? alias, AudioDevice? currentDevice = null)
    {
        return UpdateDeviceProperty(
            deviceKey,
            "alias",
            alias,
            (config, value) => config.Alias = value,
            currentDevice,
            v => $"'{v ?? "(cleared)"}'");
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
        _lock.EnterWriteLock();
        try
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
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Get all device aliases as a dictionary of sink name to alias.
    /// </summary>
    public Dictionary<string, string> GetAllDeviceAliases()
    {
        _lock.EnterReadLock();
        try
        {
            return _devices
                .Where(d => !string.IsNullOrEmpty(d.Value.Alias) && !string.IsNullOrEmpty(d.Value.LastKnownSinkName))
                .ToDictionary(d => d.Value.LastKnownSinkName!, d => d.Value.Alias!);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get all device configurations (for applying startup settings like volume limits).
    /// </summary>
    public IReadOnlyDictionary<string, DeviceConfiguration> GetAllDeviceConfigurations()
    {
        _lock.EnterReadLock();
        try
        {
            return new Dictionary<string, DeviceConfiguration>(_devices);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
