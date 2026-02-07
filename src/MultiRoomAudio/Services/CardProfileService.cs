using MultiRoomAudio.Audio;
using MultiRoomAudio.Audio.Mock;
using MultiRoomAudio.Audio.PulseAudio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages PulseAudio card profile selections with persistence.
/// Implements IHostedService to restore saved profiles on startup.
/// </summary>
public class CardProfileService
{
    private readonly ILogger<CardProfileService> _logger;
    private readonly EnvironmentService _environment;
    private readonly VolumeCommandRunner _volumeRunner;
    private readonly ConfigurationService _config;
    private readonly BackendFactory _backend;
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly ReaderWriterLockSlim _configLock = new(LockRecursionPolicy.NoRecursion);

    public CardProfileService(
        ILogger<CardProfileService> logger,
        EnvironmentService environment,
        VolumeCommandRunner volumeRunner,
        ConfigurationService config,
        BackendFactory backend)
    {
        _logger = logger;
        _environment = environment;
        _volumeRunner = volumeRunner;
        _config = config;
        _backend = backend;
        _configPath = Path.Combine(environment.ConfigPath, "card-profiles.yaml");

        // Configure logger for the enumerator
        PulseAudioCardEnumerator.SetLogger(logger);

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <summary>
    /// Restore saved card profiles on startup.
    /// Called by StartupOrchestrator during background initialization.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CardProfileService starting...");

        // Always enumerate cards to log what's available
        var cards = _environment.IsMockHardware
            ? MockCardEnumerator.GetCards().ToList()
            : PulseAudioCardEnumerator.GetCards().ToList();

        if (cards.Count == 0)
        {
            _logger.LogInformation("No sound cards detected");
        }
        else
        {
            _logger.LogInformation("Found {Count} sound card(s):", cards.Count);
            foreach (var card in cards)
            {
                var availableProfiles = card.Profiles.Count(p => p.IsAvailable);
                _logger.LogInformation(
                    "  [{Index}] {Description} ({Name}) - {ProfileCount} profiles available, active: {ActiveProfile}",
                    card.Index,
                    card.Description ?? card.Name,
                    card.Name,
                    availableProfiles,
                    card.ActiveProfile);
            }
        }

        // IMPORTANT: Load and migrate profiles BEFORE tracking cards
        // Otherwise EnsureCardTracked would save current (potentially wrong) profiles
        // before migration can map old configs to their new stable keys
        var savedProfiles = LoadConfigurations();

        if (savedProfiles.Count == 0)
        {
            _logger.LogInformation("No saved card profiles to restore");

            // Only track cards as new if there were no saved profiles
            foreach (var card in cards)
            {
                EnsureCardTracked(card);
            }
            return;
        }

        // Migrate old-format keys and build lookup by new key format
        var migratedProfiles = MigrateAndBuildLookup(savedProfiles, cards);

        // Now track any truly new cards (cards that weren't found in migrated profiles)
        foreach (var card in cards)
        {
            var stableKey = ConfigurationService.GenerateCardKey(card);
            if (!migratedProfiles.ContainsKey(stableKey))
            {
                EnsureCardTracked(card);
            }
        }

        var restoredCount = 0;
        var failedCount = 0;

        // Now restore profiles for each current card
        foreach (var card in cards)
        {
            var stableKey = ConfigurationService.GenerateCardKey(card);

            if (!migratedProfiles.TryGetValue(stableKey, out var config))
            {
                // No saved config for this card
                continue;
            }

            try
            {
                // Check if already at the desired profile
                if (card.ActiveProfile.Equals(config.ProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "Card '{CardName}' already at profile '{Profile}'",
                        card.Name, config.ProfileName);
                    restoredCount++;
                    await ApplyBootMutePreferenceAsync(card, defaultUnmute: false, logBootAction: true);
                    continue;
                }

                // Verify profile exists and is available
                var profile = card.Profiles.FirstOrDefault(p =>
                    p.Name.Equals(config.ProfileName, StringComparison.OrdinalIgnoreCase));

                if (profile == null)
                {
                    _logger.LogWarning(
                        "Saved profile '{Profile}' for card '{CardName}' not found",
                        config.ProfileName, card.Name);
                    failedCount++;
                    continue;
                }

                if (!profile.IsAvailable)
                {
                    _logger.LogWarning(
                        "Saved profile '{Profile}' for card '{CardName}' is not available",
                        config.ProfileName, card.Name);
                    failedCount++;
                    continue;
                }

                // Apply the profile
                var success = _environment.IsMockHardware
                    ? MockCardEnumerator.SetCardProfile(card.Name, config.ProfileName, out var error)
                    : PulseAudioCardEnumerator.SetCardProfile(card.Name, config.ProfileName, out error);

                if (success)
                {
                    _logger.LogInformation(
                        "Restored card '{CardName}' to profile '{Profile}'",
                        card.Name, config.ProfileName);
                    restoredCount++;
                    await ApplyBootMutePreferenceAsync(card, defaultUnmute: false, logBootAction: true);
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to restore profile '{Profile}' for card '{CardName}': {Error}",
                        config.ProfileName, card.Name, error);
                    failedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception restoring profile for card '{CardName}'", card.Name);
                failedCount++;
            }
        }

        _logger.LogInformation(
            "CardProfileService started: {Restored} profiles restored, {Failed} failed",
            restoredCount, failedCount);

        // Apply saved device volume limits after profile restoration
        await ApplyDeviceVolumeLimitsAsync();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Gets all available sound cards with their profiles.
    /// Also tracks any new cards for persistence (handles hot-plug).
    /// </summary>
    public IEnumerable<PulseAudioCard> GetCards()
    {
        var cards = _environment.IsMockHardware
            ? MockCardEnumerator.GetCards().ToList()
            : PulseAudioCardEnumerator.GetCards().ToList();

        // Track any new cards (handles hot-plug scenarios)
        foreach (var card in cards)
        {
            EnsureCardTracked(card);
        }

        var savedProfiles = LoadConfigurations();

        return cards.Select(card =>
        {
            // Try stable key first, fall back to legacy card.Name for pre-migration reads
            // (migration runs in background after Kestrel starts, so API calls may arrive first)
            var stableKey = ConfigurationService.GenerateCardKey(card);
            var config = savedProfiles.GetValueOrDefault(stableKey)
                ?? savedProfiles.GetValueOrDefault(card.Name);

            var isMuted = GetCardMuteState(card);
            var bootMuted = config?.BootMuted;
            var bootMatches = bootMuted.HasValue && isMuted.HasValue && bootMuted.Value == isMuted.Value;
            var maxVolume = GetCardMaxVolume(card);

            return card with
            {
                IsMuted = isMuted,
                BootMuted = bootMuted,
                BootMuteMatchesCurrent = bootMatches,
                MaxVolume = maxVolume
            };
        });
    }

    /// <summary>
    /// Gets a specific card by name or index.
    /// </summary>
    public PulseAudioCard? GetCard(string cardNameOrIndex)
    {
        var card = PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        if (card == null)
        {
            return null;
        }

        var savedProfiles = LoadConfigurations();
        // Try stable key first, fall back to legacy card.Name for pre-migration reads
        // (migration runs in background after Kestrel starts, so API calls may arrive first)
        var stableKey = ConfigurationService.GenerateCardKey(card);
        var config = savedProfiles.GetValueOrDefault(stableKey)
            ?? savedProfiles.GetValueOrDefault(card.Name);

        var isMuted = GetCardMuteState(card);
        var bootMuted = config?.BootMuted;
        var bootMatches = bootMuted.HasValue && isMuted.HasValue && bootMuted.Value == isMuted.Value;
        var maxVolume = GetCardMaxVolume(card);

        return card with
        {
            IsMuted = isMuted,
            BootMuted = bootMuted,
            BootMuteMatchesCurrent = bootMatches,
            MaxVolume = maxVolume
        };
    }

    /// <summary>
    /// Sets the active profile for a card and persists the selection.
    /// </summary>
    public async Task<CardProfileResponse> SetCardProfileAsync(string cardNameOrIndex, string profileName)
    {
        // Get current card state before change
        var card = _environment.IsMockHardware
            ? MockCardEnumerator.GetCard(cardNameOrIndex)
            : PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        if (card == null)
        {
            return new CardProfileResponse(
                Success: false,
                Message: $"Card '{cardNameOrIndex}' not found."
            );
        }

        var previousProfile = card.ActiveProfile;

        // Attempt to change the profile
        var success = _environment.IsMockHardware
            ? MockCardEnumerator.SetCardProfile(card.Name, profileName, out var error)
            : PulseAudioCardEnumerator.SetCardProfile(card.Name, profileName, out error);

        if (!success)
        {
            return new CardProfileResponse(
                Success: false,
                Message: error ?? "Failed to set profile.",
                CardName: card.Name,
                ActiveProfile: previousProfile
            );
        }

        // Save to persistent config using stable key
        SaveProfile(card, profileName);

        _logger.LogInformation(
            "Changed card '{Card}' profile from '{Previous}' to '{New}'",
            card.Name, previousProfile, profileName);

        // Give PulseAudio a moment to create the new sinks
        await Task.Delay(500);

        await ApplyBootMutePreferenceAsync(card, defaultUnmute: true);

        return new CardProfileResponse(
            Success: true,
            Message: $"Profile changed to '{profileName}'.",
            CardName: card.Name,
            ActiveProfile: profileName,
            PreviousProfile: previousProfile
        );
    }

    /// <summary>
    /// Sets the active profile for a card and persists the selection.
    /// Synchronous wrapper for backwards compatibility.
    /// </summary>
    public CardProfileResponse SetCardProfile(string cardNameOrIndex, string profileName)
    {
        return SetCardProfileAsync(cardNameOrIndex, profileName).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets all saved profile configurations.
    /// </summary>
    public IReadOnlyDictionary<string, CardProfileConfiguration> GetSavedProfiles()
    {
        return LoadConfigurations();
    }

    /// <summary>
    /// Ensures a card is tracked in the configuration.
    /// Returns true if a NEW card entry was created (not already tracked).
    /// Tracks the card with its current active profile.
    /// </summary>
    public bool EnsureCardTracked(PulseAudioCard card)
    {
        var stableKey = ConfigurationService.GenerateCardKey(card);
        var existingConfigs = LoadConfigurations();

        if (existingConfigs.ContainsKey(stableKey))
        {
            // Already tracked
            return false;
        }

        // New card - save with current profile
        _logger.LogInformation("New sound card discovered: {Key} ({CardName})", stableKey, card.Name);
        SaveProfile(card, card.ActiveProfile);
        return true;
    }

    /// <summary>
    /// Removes a saved profile configuration for a card.
    /// </summary>
    public bool RemoveSavedProfile(string cardNameOrIndex)
    {
        // Resolve card name if given an index
        var card = _environment.IsMockHardware
            ? MockCardEnumerator.GetCard(cardNameOrIndex)
            : PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        var cardName = card?.Name ?? cardNameOrIndex;

        return RemoveProfile(cardName);
    }

    /// <summary>
    /// Sets the mute state for all sinks belonging to a card (real-time).
    /// </summary>
    public async Task<CardMuteResponse> SetCardMuteAsync(string cardNameOrIndex, bool muted)
    {
        var card = _environment.IsMockHardware
            ? MockCardEnumerator.GetCard(cardNameOrIndex)
            : PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        if (card == null)
        {
            return new CardMuteResponse(false, $"Card '{cardNameOrIndex}' not found.");
        }

        var displayName = GetCardDisplayName(card);
        var previousState = GetCardMuteState(card);
        _logger.LogInformation(
            "Realtime mute requested for card '{Card}' to {State}",
            displayName,
            muted ? "muted" : "unmuted");

        var sinks = _environment.IsMockHardware
            ? MockCardEnumerator.GetSinksByCard(card.Name)
            : PulseAudioCardEnumerator.GetSinksByCard(card.Name);
        if (sinks.Count == 0)
        {
            return new CardMuteResponse(false, $"No sinks found for card '{card.Name}'.", CardOperationStatus.Error, card.Name);
        }

        var failed = new List<string>();
        foreach (var sinkName in sinks)
        {
            try
            {
                bool success;
                if (_environment.IsMockHardware)
                {
                    // Use mock implementation
                    success = MockCardEnumerator.SetMuteBySink(sinkName, muted);
                }
                else
                {
                    success = await _volumeRunner.SetMuteAsync(sinkName, muted);
                }
                if (!success)
                {
                    failed.Add(sinkName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set mute for sink '{Sink}'", sinkName);
                failed.Add(sinkName);
            }
        }

        if (failed.Count > 0)
        {
            return new CardMuteResponse(false,
                $"Failed to set mute for {failed.Count} sink(s): {string.Join(", ", failed)}",
                CardOperationStatus.Error,
                card.Name,
                GetCardMuteState(card));
        }

        if (previousState == true && !muted)
        {
            _logger.LogInformation(
                "Card '{Card}' changed from muted to unmuted",
                displayName);
        }

        return new CardMuteResponse(true,
            muted ? "Card muted." : "Card unmuted.",
            CardOperationStatus.Success,
            card.Name,
            muted);
    }

    /// <summary>
    /// Sets the boot mute preference for a card and persists it.
    /// </summary>
    public CardBootMuteResponse SetCardBootMute(string cardNameOrIndex, bool muted)
    {
        var card = _environment.IsMockHardware
            ? MockCardEnumerator.GetCard(cardNameOrIndex)
            : PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        if (card == null)
        {
            return new CardBootMuteResponse(false, $"Card '{cardNameOrIndex}' not found.");
        }

        var savedProfiles = LoadConfigurations();
        // Look up by stable key first, fall back to card.Name for legacy configs
        var stableKey = ConfigurationService.GenerateCardKey(card);
        var existing = savedProfiles.GetValueOrDefault(stableKey)
            ?? savedProfiles.GetValueOrDefault(card.Name);
        var previousPreference = existing?.BootMuted;

        SaveBootMute(card, muted);

        var displayName = GetCardDisplayName(card);
        var mutedLabel = muted ? "muted" : "unmuted";
        if (previousPreference.HasValue)
        {
            if (previousPreference.Value != muted)
            {
                _logger.LogInformation(
                    "Boot mute preference changed for card '{Card}': {Previous} to {Current}",
                    displayName,
                    previousPreference.Value ? "muted" : "unmuted",
                    mutedLabel);
            }
        }
        else
        {
            _logger.LogInformation(
                "Boot mute preference set for card '{Card}' to {Current}",
                displayName,
                mutedLabel);
        }

        return new CardBootMuteResponse(true,
            muted ? "Card will boot muted." : "Card will boot unmuted.",
            card.Name,
            muted);
    }

    /// <summary>
    /// Sets the maximum volume limit for a card's sinks and persists it.
    /// </summary>
    public async Task<CardMaxVolumeResponse> SetCardMaxVolumeAsync(string cardNameOrIndex, int? maxVolume)
    {
        var card = _environment.IsMockHardware
            ? MockCardEnumerator.GetCard(cardNameOrIndex)
            : PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        if (card == null)
        {
            return new CardMaxVolumeResponse(false, $"Card '{cardNameOrIndex}' not found.");
        }

        // Get all sinks for this card
        var sinks = _environment.IsMockHardware
            ? MockCardEnumerator.GetSinksByCard(card.Name)
            : PulseAudioCardEnumerator.GetSinksByCard(card.Name);
        if (sinks.Count == 0)
        {
            return new CardMaxVolumeResponse(false, $"No sinks found for card '{card.Name}'.", card.Name);
        }

        var displayName = GetCardDisplayName(card);

        // Apply volume limit to all sinks
        var failed = new List<string>();
        foreach (var sinkName in sinks)
        {
            try
            {
                if (_environment.IsMockHardware)
                {
                    // Use mock implementation
                    MockCardEnumerator.SetMaxVolumeBySink(sinkName, maxVolume);
                }
                else if (maxVolume.HasValue)
                {
                    await _volumeRunner.SetVolumeAsync(sinkName, maxVolume.Value);
                }

                // Save to device configuration for each sink
                var devices = _backend.GetOutputDevices();
                var device = devices.FirstOrDefault(d => d.Id == sinkName);
                if (device != null)
                {
                    var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                    _config.SetDeviceMaxVolume(deviceKey, maxVolume, device);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set max volume for sink '{Sink}'", sinkName);
                failed.Add(sinkName);
            }
        }

        if (failed.Count > 0)
        {
            return new CardMaxVolumeResponse(false,
                $"Failed to set max volume for {failed.Count} sink(s): {string.Join(", ", failed)}",
                card.Name);
        }

        _logger.LogInformation(
            "Set max volume for card '{Card}' to {MaxVolume}%",
            displayName,
            maxVolume?.ToString() ?? "(cleared)");

        return new CardMaxVolumeResponse(true,
            maxVolume.HasValue ? $"Max volume set to {maxVolume}%." : "Max volume limit cleared (using default 100%).",
            card.Name,
            maxVolume);
    }

    private Dictionary<string, CardProfileConfiguration> LoadConfigurations()
    {
        // Read file content outside the lock to avoid blocking on slow I/O
        string? yaml = null;
        bool fileExists;

        try
        {
            fileExists = File.Exists(_configPath);
            if (fileExists)
            {
                yaml = File.ReadAllText(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read card profiles configuration from {Path}", _configPath);
            return new Dictionary<string, CardProfileConfiguration>();
        }

        // Process the data under a read lock
        _configLock.EnterReadLock();
        try
        {
            if (!fileExists)
            {
                _logger.LogDebug("Card profiles config not found at {Path}", _configPath);
                return new Dictionary<string, CardProfileConfiguration>();
            }

            if (string.IsNullOrWhiteSpace(yaml))
                return new Dictionary<string, CardProfileConfiguration>();

            var dict = _deserializer.Deserialize<Dictionary<string, CardProfileConfiguration>>(yaml);
            if (dict == null)
                return new Dictionary<string, CardProfileConfiguration>();

            // Ensure CardName field matches dictionary key
            foreach (var (name, config) in dict)
            {
                config.CardName = name;
            }

            _logger.LogDebug("Loaded {Count} saved card profile configurations", dict.Count);
            return dict;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse card profiles configuration from {Path}", _configPath);
            return new Dictionary<string, CardProfileConfiguration>();
        }
        finally
        {
            _configLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Migrates old-format keys (card names) to new-format keys (bus path based) and builds a lookup dictionary.
    /// Old-format keys start with "alsa_card." while new-format keys start with "path_", "serial_", or "usb_".
    /// </summary>
    private Dictionary<string, CardProfileConfiguration> MigrateAndBuildLookup(
        Dictionary<string, CardProfileConfiguration> savedProfiles,
        List<PulseAudioCard> currentCards)
    {
        var result = new Dictionary<string, CardProfileConfiguration>(StringComparer.OrdinalIgnoreCase);
        var needsSave = false;

        foreach (var (key, config) in savedProfiles)
        {
            // Check if this is an old-format key (card name)
            if (key.StartsWith("alsa_card.", StringComparison.OrdinalIgnoreCase))
            {
                // Old format - find matching card by name and generate new key
                var card = currentCards.FirstOrDefault(c =>
                    c.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Equals(config.CardName, StringComparison.OrdinalIgnoreCase));

                if (card != null)
                {
                    var newKey = ConfigurationService.GenerateCardKey(card);
                    result[newKey] = config;
                    config.CardName = card.Name; // Update to current name
                    needsSave = true;
                    _logger.LogInformation(
                        "Migrated card profile key '{OldKey}' to '{NewKey}'", key, newKey);
                }
                else
                {
                    // Card not currently connected - keep old entry for now
                    result[key] = config;
                    _logger.LogDebug(
                        "Kept old-format key '{OldKey}' (card not connected)", key);
                }
            }
            else
            {
                // Already new format
                result[key] = config;
            }
        }

        // Save migrated config if any keys were changed
        if (needsSave)
        {
            SaveAllConfigurations(result);
        }

        return result;
    }

    /// <summary>
    /// Saves all configurations to the config file (used for migration).
    /// </summary>
    private void SaveAllConfigurations(Dictionary<string, CardProfileConfiguration> configs)
    {
        _configLock.EnterWriteLock();
        try
        {
            var yaml = _serializer.Serialize(configs);
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_configPath, yaml);
            _logger.LogInformation("Saved migrated card profiles configuration");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save migrated card profiles configuration");
        }
        finally
        {
            _configLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes old-format key (card.Name) if it exists during save operations.
    /// This cleans up legacy keys that weren't converted during migration.
    /// </summary>
    private void RemoveOldFormatKeyIfExists(Dictionary<string, CardProfileConfiguration> configs, string cardName, string context)
    {
        if (configs.ContainsKey(cardName))
        {
            configs.Remove(cardName);
            _logger.LogDebug("Removed old-format key '{OldKey}' during {Context}", cardName, context);
        }
    }

    private void SaveProfile(PulseAudioCard card, string profileName)
    {
        var stableKey = ConfigurationService.GenerateCardKey(card);

        // Read existing config file outside the lock
        string? existingYaml = null;
        try
        {
            if (File.Exists(_configPath))
            {
                existingYaml = File.ReadAllText(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read existing card profiles config");
        }

        // Process and serialize under write lock
        string yamlToWrite;
        _configLock.EnterWriteLock();
        try
        {
            var configs = new Dictionary<string, CardProfileConfiguration>();

            if (!string.IsNullOrWhiteSpace(existingYaml))
            {
                try
                {
                    configs = _deserializer.Deserialize<Dictionary<string, CardProfileConfiguration>>(existingYaml)
                        ?? new Dictionary<string, CardProfileConfiguration>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse existing card profiles config, starting fresh");
                }
            }

            RemoveOldFormatKeyIfExists(configs, card.Name, "profile save");

            // Add or update using stable key
            if (configs.TryGetValue(stableKey, out var existing))
            {
                existing.CardName = card.Name;
                existing.ProfileName = profileName;
            }
            else
            {
                configs[stableKey] = new CardProfileConfiguration
                {
                    CardName = card.Name,
                    ProfileName = profileName
                };
            }

            yamlToWrite = _serializer.Serialize(configs);
        }
        finally
        {
            _configLock.ExitWriteLock();
        }

        // Write file outside the lock
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_configPath, yamlToWrite);
            _logger.LogDebug("Saved card profile configuration for '{CardName}' (key: {Key})", card.Name, stableKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save card profile configuration");
        }
    }

    private bool RemoveProfile(string cardName)
    {
        // Read existing config file outside the lock
        string? existingYaml = null;
        bool fileExists;
        try
        {
            fileExists = File.Exists(_configPath);
            if (fileExists)
            {
                existingYaml = File.ReadAllText(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read card profiles config for removal");
            return false;
        }

        if (!fileExists || string.IsNullOrWhiteSpace(existingYaml))
            return false;

        // Process under write lock
        string? yamlToWrite = null;
        bool removed;
        _configLock.EnterWriteLock();
        try
        {
            var configs = _deserializer.Deserialize<Dictionary<string, CardProfileConfiguration>>(existingYaml)
                ?? new Dictionary<string, CardProfileConfiguration>();

            removed = configs.Remove(cardName);
            if (removed)
            {
                yamlToWrite = _serializer.Serialize(configs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process removal of saved profile for card '{CardName}'", cardName);
            return false;
        }
        finally
        {
            _configLock.ExitWriteLock();
        }

        // Write file outside the lock if we removed the card
        if (removed && yamlToWrite != null)
        {
            try
            {
                File.WriteAllText(_configPath, yamlToWrite);
                _logger.LogDebug("Removed saved profile for card '{CardName}'", cardName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save config after removing profile for card '{CardName}'", cardName);
                return false;
            }
        }

        return removed;
    }

    private void SaveBootMute(PulseAudioCard card, bool muted)
    {
        var stableKey = ConfigurationService.GenerateCardKey(card);

        // Read existing config file outside the lock
        string? existingYaml = null;
        try
        {
            if (File.Exists(_configPath))
            {
                existingYaml = File.ReadAllText(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read existing card profiles config");
        }

        // Process and serialize under write lock
        string yamlToWrite;
        _configLock.EnterWriteLock();
        try
        {
            var configs = new Dictionary<string, CardProfileConfiguration>();

            if (!string.IsNullOrWhiteSpace(existingYaml))
            {
                try
                {
                    configs = _deserializer.Deserialize<Dictionary<string, CardProfileConfiguration>>(existingYaml)
                        ?? new Dictionary<string, CardProfileConfiguration>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse existing card profiles config, starting fresh");
                }
            }

            RemoveOldFormatKeyIfExists(configs, card.Name, "boot mute save");

            // Add or update using stable key
            if (configs.TryGetValue(stableKey, out var existing))
            {
                existing.CardName = card.Name;
                existing.BootMuted = muted;
                existing.ProfileName = card.ActiveProfile;
            }
            else
            {
                configs[stableKey] = new CardProfileConfiguration
                {
                    CardName = card.Name,
                    ProfileName = card.ActiveProfile,
                    BootMuted = muted
                };
            }

            yamlToWrite = _serializer.Serialize(configs);
        }
        finally
        {
            _configLock.ExitWriteLock();
        }

        // Write file outside the lock
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_configPath, yamlToWrite);
            _logger.LogDebug("Saved boot mute configuration for '{CardName}' (key: {Key})", card.Name, stableKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save boot mute configuration");
        }
    }

    private bool? GetCardMuteState(PulseAudioCard card)
    {
        var sinks = _environment.IsMockHardware
            ? MockCardEnumerator.GetSinksByCard(card.Name)
            : PulseAudioCardEnumerator.GetSinksByCard(card.Name);
        if (sinks.Count == 0)
        {
            return null;
        }

        try
        {
            var output = _environment.IsMockHardware
                ? MockCardEnumerator.GetSinksMuteStates()
                : PulseAudioCardEnumerator.GetSinksMuteStates();
            if (output.Count == 0)
            {
                return null;
            }

            var sinkMutes = sinks
                .Select(sink => output.TryGetValue(sink, out var muted) ? muted : (bool?)null)
                .ToList();

            if (sinkMutes.Any(state => state == true))
            {
                return true;
            }

            if (sinkMutes.All(state => state == false))
            {
                return false;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine mute state for card {Card}", card.Name);
            return null;
        }
    }

    private int? GetCardMaxVolume(PulseAudioCard card)
    {
        var sinks = _environment.IsMockHardware
            ? MockCardEnumerator.GetSinksByCard(card.Name)
            : PulseAudioCardEnumerator.GetSinksByCard(card.Name);
        if (sinks.Count == 0)
        {
            return null;
        }

        try
        {
            // Get all device configurations
            var deviceConfigs = _config.GetAllDeviceConfigurations();
            var devices = _backend.GetOutputDevices();

            // Find the first sink that has a max volume configured
            foreach (var sinkName in sinks)
            {
                var device = devices.FirstOrDefault(d => d.Id == sinkName);
                if (device != null)
                {
                    var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                    if (deviceConfigs.TryGetValue(deviceKey, out var config) && config.MaxVolume.HasValue)
                    {
                        return config.MaxVolume.Value;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine max volume for card {Card}", card.Name);
            return null;
        }
    }

    private async Task ApplyBootMutePreferenceAsync(PulseAudioCard card, bool defaultUnmute, bool logBootAction = false)
    {
        var savedProfiles = LoadConfigurations();
        // Look up by stable key first, fall back to card.Name for legacy configs
        var stableKey = ConfigurationService.GenerateCardKey(card);
        if (!savedProfiles.TryGetValue(stableKey, out var config))
        {
            // Try legacy key (card name) as fallback
            savedProfiles.TryGetValue(card.Name, out config);
        }
        if (config == null && !defaultUnmute)
        {
            return;
        }

        if (config?.BootMuted == null && !defaultUnmute)
        {
            return;
        }

        var desiredMuted = config?.BootMuted ?? false;
        var sinks = _environment.IsMockHardware
            ? MockCardEnumerator.GetSinksByCard(card.Name)
            : PulseAudioCardEnumerator.GetSinksByCard(card.Name);
        var previousState = logBootAction ? GetCardMuteState(card) : null;
        foreach (var sinkName in sinks)
        {
            try
            {
                var success = await _volumeRunner.SetMuteAsync(sinkName, desiredMuted);
                if (success)
                {
                    _logger.LogDebug("Set sink '{Sink}' mute to {Muted} after profile restore", sinkName, desiredMuted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set mute for sink '{Sink}' after profile restore", sinkName);
            }
        }

        if (logBootAction && sinks.Count > 0)
        {
            var displayName = GetCardDisplayName(card);
            var desiredLabel = desiredMuted ? "muted" : "unmuted";
            if (previousState.HasValue)
            {
                var previousLabel = previousState.Value ? "muted" : "unmuted";
                if (previousState.Value == desiredMuted)
                {
                    _logger.LogInformation(
                        "Boot mute applied for card '{Card}': already {State}",
                        displayName,
                        desiredLabel);
                }
                else
                {
                    _logger.LogInformation(
                        "Boot mute applied for card '{Card}': changed from {Previous} to {State}",
                        displayName,
                        previousLabel,
                        desiredLabel);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Boot mute applied for card '{Card}': set to {State} (previous state unknown)",
                    displayName,
                    desiredLabel);
            }
        }
    }

    private static string GetCardDisplayName(PulseAudioCard card)
    {
        return string.IsNullOrWhiteSpace(card.Description) ? card.Name : card.Description;
    }

    /// <summary>
    /// Applies saved device volume limits from configuration.
    /// Called at startup after card profiles are restored.
    /// </summary>
    private async Task ApplyDeviceVolumeLimitsAsync()
    {
        try
        {
            // Get all devices from backend
            var devices = _backend.GetOutputDevices();
            if (!devices.Any())
            {
                _logger.LogDebug("No devices found for volume limit application");
                return;
            }

            // Get all device configurations
            var deviceConfigs = _config.GetAllDeviceConfigurations();
            if (deviceConfigs.Count == 0)
            {
                _logger.LogDebug("No device configurations with volume limits found");
                return;
            }

            var appliedCount = 0;
            var failedCount = 0;

            foreach (var device in devices)
            {
                try
                {
                    // Generate device key to look up configuration
                    var deviceKey = ConfigurationService.GenerateDeviceKey(device);
                    if (!deviceConfigs.TryGetValue(deviceKey, out var config))
                    {
                        continue;
                    }

                    // Apply max volume if configured
                    if (config.MaxVolume.HasValue)
                    {
                        await _backend.SetVolumeAsync(device.Id, config.MaxVolume.Value, CancellationToken.None);
                        _logger.LogInformation(
                            "Applied volume limit {Volume}% to device '{Device}'",
                            config.MaxVolume.Value,
                            device.Name);
                        appliedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply volume limit for device '{Device}'", device.Name);
                    failedCount++;
                }
            }

            if (appliedCount > 0 || failedCount > 0)
            {
                _logger.LogInformation(
                    "Device volume limits applied: {Applied} succeeded, {Failed} failed",
                    appliedCount, failedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying device volume limits at startup");
        }
    }
}
