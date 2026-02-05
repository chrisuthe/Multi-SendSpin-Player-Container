using System.Diagnostics;
using System.Text.RegularExpressions;
using MultiRoomAudio.Models;

// DeviceIdentifiers is used for card identifier extraction

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// Enumerates PulseAudio sound cards and their profiles using pactl commands.
/// Uses PactlCommandRunner for command execution with retry logic.
/// </summary>
public static partial class PulseAudioCardEnumerator
{
    private static ILogger? _logger;

    /// <summary>
    /// Configures the logger for card enumeration diagnostics.
    /// </summary>
    public static void SetLogger(ILogger? logger)
    {
        _logger = logger;
        PactlCommandRunner.SetLogger(logger);
    }

    /// <summary>
    /// Gets all available sound cards with their profiles.
    /// </summary>
    public static IEnumerable<PulseAudioCard> GetCards()
    {
        var cards = new List<PulseAudioCard>();

        try
        {
            var cardsOutput = RunPactl("list cards");
            if (string.IsNullOrEmpty(cardsOutput))
            {
                _logger?.LogWarning("pactl list cards returned empty output");
                return cards;
            }

            // Parse cards
            var cardBlocks = cardsOutput.Split(new[] { "Card #" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in cardBlocks)
            {
                try
                {
                    var card = ParseCardBlock(block);
                    if (card != null)
                    {
                        cards.Add(card);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to parse card block");
                }
            }

            _logger?.LogDebug("Found {Count} PulseAudio cards", cards.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to enumerate PulseAudio cards");
        }

        return cards;
    }

    /// <summary>
    /// Gets a specific card by name or index.
    /// </summary>
    public static PulseAudioCard? GetCard(string cardNameOrIndex)
    {
        // Try to parse as index first
        if (int.TryParse(cardNameOrIndex, out var index))
        {
            return GetCards().FirstOrDefault(c => c.Index == index);
        }

        // Search by name (exact match or partial match)
        return GetCards()
            .FirstOrDefault(c =>
                c.Name.Equals(cardNameOrIndex, StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains(cardNameOrIndex, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sets the active profile for a card.
    /// </summary>
    /// <returns>True if successful, false otherwise.</returns>
    public static bool SetCardProfile(string cardNameOrIndex, string profileName, out string? errorMessage)
    {
        // Validate inputs to prevent command injection
        if (!IsValidCardName(cardNameOrIndex))
        {
            errorMessage = "Invalid card name format.";
            return false;
        }

        if (!IsValidProfileName(profileName))
        {
            errorMessage = "Invalid profile name format.";
            return false;
        }

        // Verify card exists and profile is available
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
            errorMessage = $"Profile '{profileName}' not found on card '{card.Name}'. Available profiles: {availableProfiles}";
            return false;
        }

        if (!profile.IsAvailable)
        {
            errorMessage = $"Profile '{profileName}' is not available (hardware limitation).";
            return false;
        }

        // Execute the profile change
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pactl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Use ArgumentList to prevent shell injection
            psi.ArgumentList.Add("set-card-profile");
            psi.ArgumentList.Add(card.Name); // Use resolved card name
            psi.ArgumentList.Add(profileName);

            using var process = Process.Start(psi);
            if (process == null)
            {
                errorMessage = "Failed to start pactl process.";
                return false;
            }

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode != 0)
            {
                errorMessage = string.IsNullOrWhiteSpace(error)
                    ? $"pactl set-card-profile failed with exit code {process.ExitCode}"
                    : error.Trim();
                _logger?.LogWarning("Failed to set card profile: {Error}", errorMessage);
                return false;
            }

            _logger?.LogInformation("Set card '{Card}' profile to '{Profile}'", card.Name, profileName);
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            _logger?.LogError(ex, "Exception setting card profile");
            return false;
        }
    }

    private static PulseAudioCard? ParseCardBlock(string block)
    {
        // Extract card index from start of block
        var indexMatch = CardIndexRegex().Match(block);
        if (!indexMatch.Success)
            return null;

        var index = int.Parse(indexMatch.Groups[1].Value);

        // Extract card name
        var nameMatch = CardNameRegex().Match(block);
        if (!nameMatch.Success)
            return null;

        var cardName = nameMatch.Groups[1].Value.Trim();

        // Extract driver
        var driverMatch = DriverRegex().Match(block);
        var driver = driverMatch.Success ? driverMatch.Groups[1].Value.Trim() : "unknown";

        // Extract description from properties
        var descMatch = DeviceDescriptionRegex().Match(block);
        var description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : null;

        // Extract profiles
        var profiles = ParseProfiles(block);

        // Extract active profile
        var activeMatch = ActiveProfileRegex().Match(block);
        var activeProfile = activeMatch.Success ? activeMatch.Groups[1].Value.Trim() : "";

        // Extract stable device identifiers
        var identifiers = ParseCardIdentifiers(block);

        return new PulseAudioCard(
            Index: index,
            Name: cardName,
            Driver: driver,
            Description: description,
            Profiles: profiles,
            ActiveProfile: activeProfile,
            Identifiers: identifiers
        );
    }

    /// <summary>
    /// Extracts stable device identifiers from the Properties section of a pactl card block.
    /// These identifiers persist across reboots and can be used to re-match cards.
    /// </summary>
    private static DeviceIdentifiers? ParseCardIdentifiers(string block)
    {
        var serialMatch = DeviceSerialRegex().Match(block);
        var busPathMatch = DeviceBusPathRegex().Match(block);
        var vendorIdMatch = DeviceVendorIdRegex().Match(block);
        var productIdMatch = DeviceProductIdRegex().Match(block);
        var alsaLongCardNameMatch = AlsaLongCardNameRegex().Match(block);

        // Only create identifiers if we found at least one useful property
        if (!serialMatch.Success && !busPathMatch.Success && !vendorIdMatch.Success &&
            !productIdMatch.Success && !alsaLongCardNameMatch.Success)
        {
            return null;
        }

        return new DeviceIdentifiers(
            Serial: serialMatch.Success ? serialMatch.Groups[1].Value : null,
            BusPath: busPathMatch.Success ? busPathMatch.Groups[1].Value : null,
            VendorId: vendorIdMatch.Success ? vendorIdMatch.Groups[1].Value : null,
            ProductId: productIdMatch.Success ? productIdMatch.Groups[1].Value : null,
            AlsaLongCardName: alsaLongCardNameMatch.Success ? alsaLongCardNameMatch.Groups[1].Value : null
        );
    }

    private static List<CardProfile> ParseProfiles(string block)
    {
        var profiles = new List<CardProfile>();

        // Find the Profiles: section
        var profilesSectionMatch = ProfilesSectionRegex().Match(block);
        if (!profilesSectionMatch.Success)
            return profiles;

        var profilesSection = profilesSectionMatch.Groups[1].Value;

        // Parse each profile line
        var matches = ProfileLineRegex().Matches(profilesSection);
        foreach (Match match in matches)
        {
            var name = match.Groups[1].Value.Trim();
            var description = match.Groups[2].Value.Trim();
            var sinks = int.Parse(match.Groups[3].Value);
            var sources = int.Parse(match.Groups[4].Value);
            var priority = int.Parse(match.Groups[5].Value);
            var available = match.Groups[6].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);

            profiles.Add(new CardProfile(
                Name: name,
                Description: description,
                Sinks: sinks,
                Sources: sources,
                Priority: priority,
                IsAvailable: available
            ));
        }

        return profiles;
    }

    private static bool IsValidCardName(string name)
    {
        // Allow alphanumeric, underscores, hyphens, dots, and colons (common in PulseAudio card names)
        return !string.IsNullOrWhiteSpace(name) &&
               CardNameValidationRegex().IsMatch(name);
    }

    private static bool IsValidProfileName(string name)
    {
        // Allow alphanumeric, underscores, hyphens, dots, colons, and plus signs (common in profile names)
        return !string.IsNullOrWhiteSpace(name) &&
               ProfileNameValidationRegex().IsMatch(name);
    }

    /// <summary>
    /// Gets all sink names belonging to a specific card by matching device identifiers.
    /// Uses name-based matching instead of index-based matching for reliability.
    /// </summary>
    /// <param name="cardName">The card name (e.g., "alsa_card.pci-0000_01_00.0").</param>
    /// <returns>List of sink names belonging to the card.</returns>
    public static List<string> GetSinksByCard(string cardName)
    {
        var sinks = new List<string>();

        // Extract identifier from card name (e.g., "alsa_card.pci-0000_01_00.0" → "pci-0000_01_00.0")
        var identifier = cardName.Replace("alsa_card.", "");
        if (string.IsNullOrEmpty(identifier))
        {
            _logger?.LogWarning("Could not extract identifier from card name '{CardName}'", cardName);
            return sinks;
        }

        try
        {
            var output = RunPactl("list sinks");
            if (string.IsNullOrEmpty(output))
                return sinks;

            var sinkBlocks = output.Split(new[] { "Sink #" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in sinkBlocks)
            {
                var nameMatch = Regex.Match(block, @"Name:\s*(.+)$", RegexOptions.Multiline);
                if (!nameMatch.Success)
                    continue;

                var sinkName = nameMatch.Groups[1].Value.Trim();

                // Match by identifier in sink name (e.g., "alsa_output.pci-0000_01_00.0.analog-stereo")
                if (sinkName.Contains(identifier, StringComparison.OrdinalIgnoreCase))
                {
                    sinks.Add(sinkName);
                    _logger?.LogDebug("Found sink '{Sink}' belonging to card '{Card}'", sinkName, cardName);
                }
            }

            _logger?.LogDebug("Found {Count} sinks for card '{Card}' (identifier: {Identifier})", sinks.Count, cardName, identifier);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to enumerate sinks for card '{Card}'", cardName);
        }

        return sinks;
    }

    /// <summary>
    /// Gets mute state for all sinks.
    /// </summary>
    public static Dictionary<string, bool> GetSinksMuteStates()
    {
        var muteStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var output = RunPactl("list sinks");
            if (string.IsNullOrEmpty(output))
                return muteStates;

            var sinkBlocks = output.Split(new[] { "Sink #" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in sinkBlocks)
            {
                var nameMatch = Regex.Match(block, @"Name:\s*(.+)$", RegexOptions.Multiline);
                var muteMatch = Regex.Match(block, @"Mute:\s*(yes|no)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (!nameMatch.Success || !muteMatch.Success)
                    continue;

                var sinkName = nameMatch.Groups[1].Value.Trim();
                var isMuted = muteMatch.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                muteStates[sinkName] = isMuted;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to enumerate sink mute states");
        }

        return muteStates;
    }

    private static string? RunPactl(string arguments) => PactlCommandRunner.Run(arguments);

    // Regex patterns for parsing pactl output

    [GeneratedRegex(@"^(\d+)", RegexOptions.Multiline)]
    private static partial Regex CardIndexRegex();

    [GeneratedRegex(@"Name:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex CardNameRegex();

    [GeneratedRegex(@"Driver:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex DriverRegex();

    [GeneratedRegex(@"device\.description\s*=\s*""([^""]+)""")]
    private static partial Regex DeviceDescriptionRegex();

    // Match from "Profiles:" until "Active Profile:" or end of section
    [GeneratedRegex(@"Profiles:\s*\n((?:.*\n)*?)(?=\s*Active Profile:)", RegexOptions.Multiline)]
    private static partial Regex ProfilesSectionRegex();

    // Profile line format: "profile-name: Description (sinks: N, sources: N, priority: N, available: yes/no)"
    [GeneratedRegex(@"^\s+([^\s:]+(?::[^\s:]+)*):\s*(.+?)\s*\(sinks:\s*(\d+),\s*sources:\s*(\d+),\s*priority:\s*(\d+),\s*available:\s*(yes|no)\)", RegexOptions.Multiline)]
    private static partial Regex ProfileLineRegex();

    [GeneratedRegex(@"Active Profile:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex ActiveProfileRegex();

    // Validation patterns
    [GeneratedRegex(@"^[a-zA-Z0-9_\-.:]+$")]
    private static partial Regex CardNameValidationRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_\-.:+]+$")]
    private static partial Regex ProfileNameValidationRegex();

    // Regex patterns for stable device identifiers (from Properties section)
    [GeneratedRegex(@"device\.serial\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeviceSerialRegex();

    [GeneratedRegex(@"device\.bus_path\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeviceBusPathRegex();

    [GeneratedRegex(@"device\.vendor\.id\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeviceVendorIdRegex();

    [GeneratedRegex(@"device\.product\.id\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeviceProductIdRegex();

    [GeneratedRegex(@"alsa\.long_card_name\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex AlsaLongCardNameRegex();
}
