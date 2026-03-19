namespace MultiRoomAudio.Models.CardModels;

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
