namespace MultiRoomAudio.Services.Onboarding;

/// <summary>
/// Onboarding state persisted to YAML.
/// </summary>
public class OnboardingState
{
    /// <summary>
    /// Whether the onboarding wizard has been completed.
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// When the onboarding was completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Number of devices configured during onboarding.
    /// </summary>
    public int DevicesConfigured { get; set; }

    /// <summary>
    /// Number of players created during onboarding.
    /// </summary>
    public int PlayersCreated { get; set; }

    /// <summary>
    /// Version of the app when onboarding was completed.
    /// </summary>
    public string? AppVersion { get; set; }
}
