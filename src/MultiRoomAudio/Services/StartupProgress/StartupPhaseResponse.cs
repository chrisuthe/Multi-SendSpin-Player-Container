using System.Text.Json.Serialization;

namespace MultiRoomAudio.Services.StartupProgress;

/// <summary>
/// Response model for a single startup phase.
/// </summary>
public record StartupPhaseResponse(
    string Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StartupPhaseStatus Status,
    string? Detail
);
