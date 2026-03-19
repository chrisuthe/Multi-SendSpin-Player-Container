namespace MultiRoomAudio.Services.StartupProgress;

/// <summary>
/// Response model for the startup progress endpoint.
/// </summary>
public record StartupProgressResponse(
    bool Complete,
    List<StartupPhaseResponse> Phases
);
