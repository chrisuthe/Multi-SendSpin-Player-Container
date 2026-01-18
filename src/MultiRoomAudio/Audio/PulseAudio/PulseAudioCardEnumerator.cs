using System.Diagnostics;
using System.Text.RegularExpressions;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// Enumerates PulseAudio sound cards and their profiles using pactl commands.
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

        return new PulseAudioCard(
            Index: index,
            Name: cardName,
            Driver: driver,
            Description: description,
            Profiles: profiles,
            ActiveProfile: activeProfile
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
    /// Gets all sink names belonging to a specific card.
    /// </summary>
    /// <param name="cardIndex">The card index to find sinks for.</param>
    /// <returns>List of sink names belonging to the card.</returns>
    public static List<string> GetSinksByCard(int cardIndex)
    {
        var sinks = new List<string>();

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

                var cardMatch = Regex.Match(block, @"alsa\.card\s*=\s*""(\d+)""");
                if (!cardMatch.Success)
                {
                    cardMatch = Regex.Match(block, @"device\.card\s*=\s*""(\d+)""");
                }

                if (!cardMatch.Success)
                    continue;

                if (int.TryParse(cardMatch.Groups[1].Value, out var sinkCard) && sinkCard == cardIndex)
                {
                    var sinkName = nameMatch.Groups[1].Value.Trim();
                    sinks.Add(sinkName);
                    _logger?.LogDebug("Found sink '{Sink}' belonging to card {Card}", sinkName, cardIndex);
                }
            }

            _logger?.LogDebug("Found {Count} sinks for card {Card}", sinks.Count, cardIndex);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to enumerate sinks for card {Card}", cardIndex);
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

    /// <summary>
    /// Maximum retries for pactl commands when PulseAudio is temporarily unavailable.
    /// </summary>
    private const int MaxPactlRetries = 3;

    /// <summary>
    /// Delay between pactl retry attempts in milliseconds.
    /// </summary>
    private const int PactlRetryDelayMs = 500;

    private static string? RunPactl(string arguments)
    {
        Exception? lastException = null;
        string? lastError = null;

        for (int attempt = 1; attempt <= MaxPactlRetries; attempt++)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pactl",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    lastError = "Failed to start pactl process";
                    continue;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                {
                    lastError = error.Trim();

                    // Check if this is a connection error that might be transient
                    if (error.Contains("Connection refused") ||
                        error.Contains("Connection failure") ||
                        error.Contains("No PulseAudio daemon running"))
                    {
                        if (attempt < MaxPactlRetries)
                        {
                            _logger?.LogDebug(
                                "pactl {Args} failed (attempt {Attempt}/{Max}): {Error}. Retrying...",
                                arguments, attempt, MaxPactlRetries, lastError);
                            Thread.Sleep(PactlRetryDelayMs);
                            continue;
                        }
                    }

                    _logger?.LogWarning("pactl {Args} failed: {Error}", arguments, lastError);
                    return null;
                }

                // Success - return output
                return output;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < MaxPactlRetries)
                {
                    _logger?.LogDebug(ex,
                        "pactl {Args} threw exception (attempt {Attempt}/{Max}). Retrying...",
                        arguments, attempt, MaxPactlRetries);
                    Thread.Sleep(PactlRetryDelayMs);
                }
            }
        }

        // All retries exhausted
        if (lastException != null)
        {
            _logger?.LogError(lastException, "Failed to run pactl {Args} after {Attempts} attempts", arguments, MaxPactlRetries);
        }
        else if (lastError != null)
        {
            _logger?.LogWarning("pactl {Args} failed after {Attempts} attempts: {Error}", arguments, MaxPactlRetries, lastError);
        }

        return null;
    }

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
}
