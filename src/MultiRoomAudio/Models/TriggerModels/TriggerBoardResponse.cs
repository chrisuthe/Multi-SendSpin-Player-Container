namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Response for a single relay board status.
/// </summary>
public record TriggerBoardResponse(
    string BoardId,
    string? DisplayName,
    RelayBoardType BoardType,
    bool IsConnected,
    TriggerFeatureState State,
    int ChannelCount,
    string? UsbPath,
    bool IsPortBased,
    string? ErrorMessage,
    List<TriggerResponse> Triggers,
    int CurrentRelayStates,
    RelayStartupBehavior StartupBehavior,
    RelayStartupBehavior ShutdownBehavior
);
