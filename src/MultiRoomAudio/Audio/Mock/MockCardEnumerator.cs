using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Audio.Mock;

/// <summary>
/// Mock card enumerator for testing without real PulseAudio hardware.
/// Provides fake sound cards with typical USB DAC profiles.
/// Configuration is loaded from MockHardwareConfigService when available.
/// </summary>
public static class MockCardEnumerator
{
    private static ILogger? _logger;
    private static MockHardwareConfigService? _configService;
    private static readonly Dictionary<int, string> _activeProfiles = new();
    private static readonly Dictionary<int, bool> _muteStates = new();

    /// <summary>
    /// Configures the logger for card enumeration diagnostics.
    /// </summary>
    public static void SetLogger(ILogger? logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Configures the mock hardware config service for dynamic configuration.
    /// </summary>
    public static void SetConfigService(MockHardwareConfigService? configService)
    {
        _configService = configService;
        _logger?.LogDebug("MockCardEnumerator config service set (using {Source})",
            configService?.UsingDefaults == true ? "defaults" : "mock_hardware.yaml");
    }

    /// <summary>
    /// Gets all available mock sound cards with their profiles.
    /// </summary>
    public static IEnumerable<PulseAudioCard> GetCards()
    {
        // Get enabled cards from config service
        var cardConfigs = _configService?.GetEnabledAudioCards()
            ?? new List<MockAudioCardConfig>();

        _logger?.LogDebug("Returning {Count} mock cards", cardConfigs.Count);

        return cardConfigs.Select(config =>
        {
            var profiles = config.Profiles.Select(p => new CardProfile(
                Name: p.Name,
                Description: p.Description,
                Sinks: p.Sinks,
                Sources: 0,
                Priority: p.Priority,
                IsAvailable: p.IsAvailable
            )).ToList();

            // Determine active profile from runtime state, then config default, then first non-off
            var activeProfile = _activeProfiles.GetValueOrDefault(config.Index)
                ?? config.Profiles.FirstOrDefault(p => p.IsDefault)?.Name
                ?? profiles.FirstOrDefault(p => p.Name != "off")?.Name
                ?? "output:analog-stereo";

            var isMuted = _muteStates.GetValueOrDefault(config.Index, false);
            var maxVolume = _maxVolumes.TryGetValue(config.Index, out var vol) ? vol : (int?)null;

            return new PulseAudioCard(
                Index: config.Index,
                Name: config.Name,
                Driver: config.Driver,
                Description: config.Description,
                Profiles: profiles,
                ActiveProfile: activeProfile,
                IsMuted: isMuted,
                BootMuted: null,
                BootMuteMatchesCurrent: false,
                MaxVolume: maxVolume
            );
        }).ToList();
    }

    /// <summary>
    /// Gets all enabled card configs (internal use).
    /// </summary>
    private static IReadOnlyList<MockAudioCardConfig> GetCardConfigs()
    {
        return _configService?.GetEnabledAudioCards() ?? new List<MockAudioCardConfig>();
    }

    /// <summary>
    /// Gets a specific card by name or index.
    /// </summary>
    public static PulseAudioCard? GetCard(string cardNameOrIndex)
    {
        if (int.TryParse(cardNameOrIndex, out var index))
        {
            return GetCards().FirstOrDefault(c => c.Index == index);
        }

        return GetCards()
            .FirstOrDefault(c =>
                c.Name.Equals(cardNameOrIndex, StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains(cardNameOrIndex, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sets the active profile for a mock card.
    /// </summary>
    public static bool SetCardProfile(string cardNameOrIndex, string profileName, out string? errorMessage)
    {
        var card = GetCard(cardNameOrIndex);
        if (card == null)
        {
            errorMessage = $"Card '{cardNameOrIndex}' not found.";
            return false;
        }

        var profile = card.Profiles.FirstOrDefault(p =>
            p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
        {
            var availableProfiles = string.Join(", ", card.Profiles.Select(p => p.Name));
            errorMessage = $"Invalid profile '{profileName}'. Available profiles: {availableProfiles}";
            return false;
        }

        _activeProfiles[card.Index] = profileName;
        _logger?.LogInformation("Mock card '{Card}' profile set to '{Profile}'", card.Name, profileName);

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Gets all sink names belonging to a specific card.
    /// </summary>
    public static List<string> GetSinksByCard(int cardIndex)
    {
        var config = GetCardConfigs().FirstOrDefault(c => c.Index == cardIndex);
        if (config == null)
            return new List<string>();

        // Return the corresponding mock sink name
        var sinkId = config.Name.Replace("alsa_card.", "mock_");
        return new List<string> { sinkId };
    }

    /// <summary>
    /// Gets mute state for all sinks.
    /// </summary>
    public static Dictionary<string, bool> GetSinksMuteStates()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in GetCardConfigs())
        {
            var sinkId = config.Name.Replace("alsa_card.", "mock_");
            result[sinkId] = _muteStates.GetValueOrDefault(config.Index, false);
        }
        return result;
    }

    /// <summary>
    /// Sets mute state for a card.
    /// </summary>
    public static bool SetMute(int cardIndex, bool muted)
    {
        if (!GetCardConfigs().Any(c => c.Index == cardIndex))
            return false;

        _muteStates[cardIndex] = muted;
        _logger?.LogDebug("Mock card {Index} mute set to {Muted}", cardIndex, muted);
        return true;
    }

    /// <summary>
    /// Sets mute state for a sink by name.
    /// </summary>
    public static bool SetMuteBySink(string sinkName, bool muted)
    {
        // Find the card that owns this sink
        // Sink names from GetSinksByCard are like "mock_pci-0000_00_1f.3"
        // Card names are like "alsa_card.pci-0000_00_1f.3"
        foreach (var config in GetCardConfigs())
        {
            var mockSinkId = config.Name.Replace("alsa_card.", "mock_");
            var cardSuffix = config.Name.Replace("alsa_card.", "");

            if (mockSinkId.Equals(sinkName, StringComparison.OrdinalIgnoreCase) ||
                sinkName.Contains(cardSuffix, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("Mock: Setting mute for card {Index} (sink '{Sink}') to {Muted}",
                    config.Index, sinkName, muted);
                return SetMute(config.Index, muted);
            }
        }

        _logger?.LogWarning("Mock: Sink '{Sink}' not found for mute operation", sinkName);
        return false;
    }

    #region Max Volume Tracking

    private static readonly Dictionary<int, int> _maxVolumes = new();

    /// <summary>
    /// Gets the max volume for a card, if configured.
    /// </summary>
    public static int? GetMaxVolume(int cardIndex)
    {
        return _maxVolumes.TryGetValue(cardIndex, out var vol) ? vol : null;
    }

    /// <summary>
    /// Sets the max volume for a card.
    /// </summary>
    public static bool SetMaxVolume(int cardIndex, int? maxVolume)
    {
        if (!GetCardConfigs().Any(c => c.Index == cardIndex))
            return false;

        if (maxVolume.HasValue)
        {
            _maxVolumes[cardIndex] = Math.Clamp(maxVolume.Value, 0, 100);
            _logger?.LogDebug("Mock card {Index} max volume set to {MaxVolume}%", cardIndex, maxVolume.Value);
        }
        else
        {
            _maxVolumes.Remove(cardIndex);
            _logger?.LogDebug("Mock card {Index} max volume cleared", cardIndex);
        }
        return true;
    }

    /// <summary>
    /// Sets max volume for a sink by name.
    /// </summary>
    public static bool SetMaxVolumeBySink(string sinkName, int? maxVolume)
    {
        // Find the card that owns this sink
        // Sink names from GetSinksByCard are like "mock_pci-0000_00_1f.3"
        // Card names are like "alsa_card.pci-0000_00_1f.3"
        foreach (var config in GetCardConfigs())
        {
            var mockSinkId = config.Name.Replace("alsa_card.", "mock_");
            var cardSuffix = config.Name.Replace("alsa_card.", "");

            if (mockSinkId.Equals(sinkName, StringComparison.OrdinalIgnoreCase) ||
                sinkName.Contains(cardSuffix, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("Mock: Setting max volume for card {Index} (sink '{Sink}') to {MaxVolume}",
                    config.Index, sinkName, maxVolume?.ToString() ?? "null");
                return SetMaxVolume(config.Index, maxVolume);
            }
        }

        _logger?.LogWarning("Mock: Sink '{Sink}' not found for max volume operation", sinkName);
        return false;
    }

    #endregion

    #region Test Support

    /// <summary>
    /// Resets all mock state. Call this between tests to ensure isolation.
    /// </summary>
    public static void ResetState()
    {
        _activeProfiles.Clear();
        _muteStates.Clear();
        _maxVolumes.Clear();
        _logger?.LogDebug("Mock card state reset");
    }

    #endregion
}
