using System.Text.Json.Serialization;

namespace MultiRoomAudio.Services.StartupProgress;

/// <summary>
/// Status of a startup phase.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StartupPhaseStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}
