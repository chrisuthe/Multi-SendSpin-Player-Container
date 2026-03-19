namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Response for a single trigger channel status.
/// </summary>
public record TriggerResponse(
    int Channel,
    string? CustomSinkName,
    string? CustomSinkDisplayName,
    int OffDelaySeconds,
    string? ZoneName,
    RelayState RelayState,
    bool IsActive,
    DateTime? LastActivated,
    DateTime? ScheduledOffTime
);
