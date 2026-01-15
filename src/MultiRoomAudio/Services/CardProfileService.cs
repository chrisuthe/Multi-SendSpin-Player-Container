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
public class CardProfileService : IHostedService
{
    private readonly ILogger<CardProfileService> _logger;
    private readonly EnvironmentService _environment;
    private readonly VolumeCommandRunner _volumeRunner;
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly object _configLock = new();

    public CardProfileService(
        ILogger<CardProfileService> logger,
        EnvironmentService environment,
        VolumeCommandRunner volumeRunner)
    {
        _logger = logger;
        _environment = environment;
        _volumeRunner = volumeRunner;
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
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CardProfileService starting...");

        // Always enumerate cards to log what's available
        var cards = PulseAudioCardEnumerator.GetCards().ToList();

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

        var savedProfiles = LoadConfigurations();

        if (savedProfiles.Count == 0)
        {
            _logger.LogInformation("No saved card profiles to restore");
            return;
        }
        var restoredCount = 0;
        var failedCount = 0;

        foreach (var (cardName, config) in savedProfiles)
        {
            try
            {
                // Find the card
                var card = cards.FirstOrDefault(c =>
                    c.Name.Equals(cardName, StringComparison.OrdinalIgnoreCase));

                if (card == null)
                {
                    _logger.LogWarning(
                        "Saved profile for card '{CardName}' could not be restored: card not found",
                        cardName);
                    failedCount++;
                    continue;
                }

                // Check if already at the desired profile
                if (card.ActiveProfile.Equals(config.ProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "Card '{CardName}' already at profile '{Profile}'",
                        cardName, config.ProfileName);
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
                        config.ProfileName, cardName);
                    failedCount++;
                    continue;
                }

                if (!profile.IsAvailable)
                {
                    _logger.LogWarning(
                        "Saved profile '{Profile}' for card '{CardName}' is not available",
                        config.ProfileName, cardName);
                    failedCount++;
                    continue;
                }

                // Apply the profile
                if (PulseAudioCardEnumerator.SetCardProfile(card.Name, config.ProfileName, out var error))
                {
                    _logger.LogInformation(
                        "Restored card '{CardName}' to profile '{Profile}'",
                        cardName, config.ProfileName);
                    restoredCount++;
                    await ApplyBootMutePreferenceAsync(card, defaultUnmute: false, logBootAction: true);
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to restore profile '{Profile}' for card '{CardName}': {Error}",
                        config.ProfileName, cardName, error);
                    failedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception restoring profile for card '{CardName}'", cardName);
                failedCount++;
            }
        }

        _logger.LogInformation(
            "CardProfileService started: {Restored} profiles restored, {Failed} failed",
            restoredCount, failedCount);

        await Task.CompletedTask;
    }

    /// <summary>
    /// No-op on shutdown (profiles persist in PulseAudio until system restart).
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CardProfileService stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets all available sound cards with their profiles.
    /// </summary>
    public IEnumerable<PulseAudioCard> GetCards()
    {
        var cards = PulseAudioCardEnumerator.GetCards().ToList();
        var savedProfiles = LoadConfigurations();

        return cards.Select(card =>
        {
            var config = savedProfiles.GetValueOrDefault(card.Name);
            var isMuted = GetCardMuteState(card);
            var bootMuted = config?.BootMuted;
            var bootMatches = bootMuted.HasValue && isMuted.HasValue && bootMuted.Value == isMuted.Value;

            return card with
            {
                IsMuted = isMuted,
                BootMuted = bootMuted,
                BootMuteMatchesCurrent = bootMatches
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

        var config = LoadConfigurations().GetValueOrDefault(card.Name);
        var isMuted = GetCardMuteState(card);
        var bootMuted = config?.BootMuted;
        var bootMatches = bootMuted.HasValue && isMuted.HasValue && bootMuted.Value == isMuted.Value;

        return card with
        {
            IsMuted = isMuted,
            BootMuted = bootMuted,
            BootMuteMatchesCurrent = bootMatches
        };
    }

    /// <summary>
    /// Sets the active profile for a card and persists the selection.
    /// </summary>
    public async Task<CardProfileResponse> SetCardProfileAsync(string cardNameOrIndex, string profileName)
    {
        // Get current card state before change
        var card = PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        if (card == null)
        {
            return new CardProfileResponse(
                Success: false,
                Message: $"Card '{cardNameOrIndex}' not found."
            );
        }

        var previousProfile = card.ActiveProfile;

        // Attempt to change the profile
        if (!PulseAudioCardEnumerator.SetCardProfile(card.Name, profileName, out var error))
        {
            return new CardProfileResponse(
                Success: false,
                Message: error ?? "Failed to set profile.",
                CardName: card.Name,
                ActiveProfile: previousProfile
            );
        }

        // Save to persistent config
        SaveProfile(card.Name, profileName);

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
    /// Removes a saved profile configuration for a card.
    /// </summary>
    public bool RemoveSavedProfile(string cardNameOrIndex)
    {
        // Resolve card name if given an index
        var card = PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        var cardName = card?.Name ?? cardNameOrIndex;

        return RemoveProfile(cardName);
    }

    /// <summary>
    /// Sets the mute state for all sinks belonging to a card (real-time).
    /// </summary>
    public async Task<CardMuteResponse> SetCardMuteAsync(string cardNameOrIndex, bool muted)
    {
        var card = PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
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

        var sinks = PulseAudioCardEnumerator.GetSinksByCard(card.Index);
        if (sinks.Count == 0)
        {
            return new CardMuteResponse(false, $"No sinks found for card '{card.Name}'.", card.Name);
        }

        var failed = new List<string>();
        foreach (var sinkName in sinks)
        {
            try
            {
                var success = await _volumeRunner.SetMuteAsync(sinkName, muted);
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
            card.Name,
            muted);
    }

    /// <summary>
    /// Sets the boot mute preference for a card and persists it.
    /// </summary>
    public CardBootMuteResponse SetCardBootMute(string cardNameOrIndex, bool muted)
    {
        var card = PulseAudioCardEnumerator.GetCard(cardNameOrIndex);
        if (card == null)
        {
            return new CardBootMuteResponse(false, $"Card '{cardNameOrIndex}' not found.");
        }

        var savedProfiles = LoadConfigurations();
        var previousPreference = savedProfiles.TryGetValue(card.Name, out var existing)
            ? existing.BootMuted
            : null;

        SaveBootMute(card.Name, card.ActiveProfile, muted);

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

    private Dictionary<string, CardProfileConfiguration> LoadConfigurations()
    {
        lock (_configLock)
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogDebug("Card profiles config not found at {Path}", _configPath);
                return new Dictionary<string, CardProfileConfiguration>();
            }

            try
            {
                var yaml = File.ReadAllText(_configPath);
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
                _logger.LogError(ex, "Failed to load card profiles configuration from {Path}", _configPath);
                return new Dictionary<string, CardProfileConfiguration>();
            }
        }
    }

    private void SaveProfile(string cardName, string profileName)
    {
        lock (_configLock)
        {
            var configs = new Dictionary<string, CardProfileConfiguration>();

            // Load existing
            if (File.Exists(_configPath))
            {
                try
                {
                    var yaml = File.ReadAllText(_configPath);
                    if (!string.IsNullOrWhiteSpace(yaml))
                    {
                        configs = _deserializer.Deserialize<Dictionary<string, CardProfileConfiguration>>(yaml)
                            ?? new Dictionary<string, CardProfileConfiguration>();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read existing card profiles config, starting fresh");
                }
            }

            // Add or update
            if (configs.TryGetValue(cardName, out var existing))
            {
                existing.CardName = cardName;
                existing.ProfileName = profileName;
            }
            else
            {
                configs[cardName] = new CardProfileConfiguration
                {
                    CardName = cardName,
                    ProfileName = profileName
                };
            }

            // Save
            try
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var yaml = _serializer.Serialize(configs);
                File.WriteAllText(_configPath, yaml);
                _logger.LogDebug("Saved card profile configuration for '{CardName}'", cardName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save card profile configuration");
            }
        }
    }

    private bool RemoveProfile(string cardName)
    {
        lock (_configLock)
        {
            if (!File.Exists(_configPath))
                return false;

            try
            {
                var yaml = File.ReadAllText(_configPath);
                if (string.IsNullOrWhiteSpace(yaml))
                    return false;

                var configs = _deserializer.Deserialize<Dictionary<string, CardProfileConfiguration>>(yaml)
                    ?? new Dictionary<string, CardProfileConfiguration>();

                if (configs.Remove(cardName))
                {
                    yaml = _serializer.Serialize(configs);
                    File.WriteAllText(_configPath, yaml);
                    _logger.LogDebug("Removed saved profile for card '{CardName}'", cardName);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove saved profile for card '{CardName}'", cardName);
                return false;
            }
        }
    }

    private void SaveBootMute(string cardName, string profileName, bool muted)
    {
        lock (_configLock)
        {
            var configs = new Dictionary<string, CardProfileConfiguration>();

            if (File.Exists(_configPath))
            {
                try
                {
                    var yaml = File.ReadAllText(_configPath);
                    if (!string.IsNullOrWhiteSpace(yaml))
                    {
                        configs = _deserializer.Deserialize<Dictionary<string, CardProfileConfiguration>>(yaml)
                            ?? new Dictionary<string, CardProfileConfiguration>();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read existing card profiles config, starting fresh");
                }
            }

            if (configs.TryGetValue(cardName, out var existing))
            {
                existing.CardName = cardName;
                existing.BootMuted = muted;
                existing.ProfileName = profileName;
            }
            else
            {
                configs[cardName] = new CardProfileConfiguration
                {
                    CardName = cardName,
                    ProfileName = profileName,
                    BootMuted = muted
                };
            }

            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var yaml = _serializer.Serialize(configs);
                File.WriteAllText(_configPath, yaml);
                _logger.LogDebug("Saved boot mute configuration for '{CardName}'", cardName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save boot mute configuration");
            }
        }
    }

    private bool? GetCardMuteState(PulseAudioCard card)
    {
        var sinks = PulseAudioCardEnumerator.GetSinksByCard(card.Index);
        if (sinks.Count == 0)
        {
            return null;
        }

        try
        {
            var output = PulseAudioCardEnumerator.GetSinksMuteStates();
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

    private async Task ApplyBootMutePreferenceAsync(PulseAudioCard card, bool defaultUnmute, bool logBootAction = false)
    {
        var savedProfiles = LoadConfigurations();
        if (!savedProfiles.TryGetValue(card.Name, out var config))
        {
            if (!defaultUnmute)
            {
                return;
            }
        }

        if (config?.BootMuted == null && !defaultUnmute)
        {
            return;
        }

        var desiredMuted = config?.BootMuted ?? false;
        var sinks = PulseAudioCardEnumerator.GetSinksByCard(card.Index);
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
}
