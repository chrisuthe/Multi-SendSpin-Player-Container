namespace MultiRoomAudio.Controllers;

/// <summary>
/// Request to complete onboarding.
/// </summary>
public record OnboardingCompleteRequest(int DevicesConfigured = 0, int PlayersCreated = 0);
