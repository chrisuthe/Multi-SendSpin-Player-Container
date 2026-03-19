using MultiRoomAudio.Models.DeviceInfo;

namespace MultiRoomAudio.Models.CardModels;

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
    /// <summary>Stable device identifiers (serial, bus path, etc.) for persistent matching.</summary>
    DeviceIdentifiers? Identifiers = null,
    /// <summary>Whether the card is currently muted (based on sinks).</summary>
    bool? IsMuted = null,
    /// <summary>Boot mute preference if configured.</summary>
    bool? BootMuted = null,
    /// <summary>Whether the current mute state matches the boot preference.</summary>
    bool BootMuteMatchesCurrent = false,
    /// <summary>Maximum volume limit for the card's sinks (0-100), or null if no limit.</summary>
    int? MaxVolume = null
);
