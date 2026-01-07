using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services;

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

    // Output format configuration (null = auto-detect from device)
    public int? OutputSampleRate { get; set; }
    public int? OutputBitDepth { get; set; }

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
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly object _lock = new();

    private Dictionary<string, PlayerConfiguration> _players = new();

    public ConfigurationService(
        ILogger<ConfigurationService> logger,
        EnvironmentService environment)
    {
        _logger = logger;
        _environment = environment;
        _configPath = environment.PlayersConfigPath;

        _logger.LogDebug("Initializing ConfigurationService with config path: {ConfigPath}", _configPath);

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
    }

    /// <summary>
    /// All configured players.
    /// </summary>
    public IReadOnlyDictionary<string, PlayerConfiguration> Players => _players;

    /// <summary>
    /// Load player configurations from YAML file.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            _logger.LogDebug("Loading player configuration from {ConfigPath}", _configPath);

            if (!File.Exists(_configPath))
            {
                _logger.LogInformation("Config file {ConfigPath} does not exist, starting fresh", _configPath);
                _players = new Dictionary<string, PlayerConfiguration>();
                return;
            }

            try
            {
                var yaml = File.ReadAllText(_configPath);

                // Handle empty or whitespace-only YAML files
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    _logger.LogInformation("Config file {ConfigPath} is empty, starting fresh", _configPath);
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
                    _configPath);
                _players = new Dictionary<string, PlayerConfiguration>();
            }
            catch (IOException ex)
            {
                _logger.LogError(ex,
                    "Failed to read configuration file {ConfigPath}. Check file permissions",
                    _configPath);
                _players = new Dictionary<string, PlayerConfiguration>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error loading config from {ConfigPath}", _configPath);
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
                File.WriteAllText(_configPath, yaml);
                _logger.LogInformation("Configuration saved successfully ({PlayerCount} players)",
                    _players.Count);
                return true;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex,
                    "Failed to write configuration file {ConfigPath}. Check disk space and permissions",
                    _configPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saving config to {ConfigPath}", _configPath);
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
}
