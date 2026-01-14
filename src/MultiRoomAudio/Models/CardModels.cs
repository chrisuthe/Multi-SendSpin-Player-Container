using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models;

/// <summary>
/// A PulseAudio card profile representing an audio configuration mode.
/// For example: stereo output, 5.1 surround, 7.1 surround, etc.
/// </summary>
public record CardProfile(
    /// <summary>Profile name (e.g., "output:analog-stereo", "output:analog-surround-71").</summary>
    string Name,
    /// <summary>Human-readable description (e.g., "Analog Stereo Output").</summary>
    string Description,
    /// <summary>Number of sinks this profile creates.</summary>
    int Sinks,
    /// <summary>Number of sources (inputs) this profile creates.</summary>
    int Sources,
    /// <summary>Priority value for automatic profile selection.</summary>
    int Priority,
    /// <summary>Whether this profile is currently available for use.</summary>
    bool IsAvailable
);

/// <summary>
/// A PulseAudio sound card with its available profiles.
/// </summary>
public record PulseAudioCard(
    /// <summary>Card index (numeric identifier).</summary>
    int Index,
    /// <summary>Card name (e.g., "alsa_card.usb-Creative_Sound_Blaster-00").</summary>
    string Name,
    /// <summary>Driver module name.</summary>
    string Driver,
    /// <summary>Human-readable description from device properties.</summary>
    string? Description,
    /// <summary>List of available profiles for this card.</summary>
    List<CardProfile> Profiles,
    /// <summary>Currently active profile name.</summary>
    string ActiveProfile,
    /// <summary>Whether the card is currently muted (based on sinks).</summary>
    bool? IsMuted = null,
    /// <summary>Boot mute preference if configured.</summary>
    bool? BootMuted = null,
    /// <summary>Whether the current mute state matches the boot preference.</summary>
    bool BootMuteMatchesCurrent = false
);

/// <summary>
/// Response containing list of sound cards.
/// </summary>
public record CardsListResponse(
    List<PulseAudioCard> Cards,
    int Count
);

/// <summary>
/// Request to set a card's active profile.
/// </summary>
public class SetCardProfileRequest
{
    /// <summary>
    /// Profile name to activate (e.g., "output:analog-surround-71").
    /// Must be one of the available profiles for the card.
    /// </summary>
    [Required(ErrorMessage = "Profile name is required.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Profile name must be 1-200 characters.")]
    public required string Profile { get; set; }
}

/// <summary>
/// Configuration for a persisted card profile selection.
/// </summary>
public class CardProfileConfiguration
{
    /// <summary>
    /// Card name (PulseAudio card name, not index, for stability across restarts).
    /// </summary>
    public required string CardName { get; set; }

    /// <summary>
    /// Selected profile name.
    /// </summary>
    public required string ProfileName { get; set; }

    /// <summary>
    /// Boot mute preference (true = muted, false = unmuted).
    /// </summary>
    public bool? BootMuted { get; set; }
}

/// <summary>
/// Response for card profile operations.
/// </summary>
public record CardProfileResponse(
    bool Success,
    string Message,
    string? CardName = null,
    string? ActiveProfile = null,
    string? PreviousProfile = null
);

/// <summary>
/// Request to set a card's mute state.
/// </summary>
public class SetCardMuteRequest
{
    /// <summary>
    /// True to mute the card, false to unmute it.
    /// </summary>
    [Required(ErrorMessage = "Mute state is required.")]
    public bool Muted { get; set; }
}

/// <summary>
/// Request to set a card's boot mute preference.
/// </summary>
public class SetCardBootMuteRequest
{
    /// <summary>
    /// True to mute the card at boot, false to unmute it at boot.
    /// </summary>
    [Required(ErrorMessage = "Boot mute state is required.")]
    public bool Muted { get; set; }
}

/// <summary>
/// Response for card mute operations.
/// </summary>
public record CardMuteResponse(
    bool Success,
    string Message,
    string? CardName = null,
    bool? IsMuted = null
);

/// <summary>
/// Response for card boot mute operations.
/// </summary>
public record CardBootMuteResponse(
    bool Success,
    string Message,
    string? CardName = null,
    bool? BootMuted = null
);
