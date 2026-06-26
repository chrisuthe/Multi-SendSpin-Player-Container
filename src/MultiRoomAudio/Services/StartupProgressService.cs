using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using MultiRoomAudio.Hubs;

namespace MultiRoomAudio.Services;

/// <summary>
/// Tracks startup initialization progress and broadcasts updates to web clients via SignalR.
/// </summary>
public class StartupProgressService
{
    private readonly ILogger<StartupProgressService> _logger;
    private readonly ILogger _triggerLogger;
    private readonly IHubContext<PlayerStatusHub> _hubContext;
    private readonly object _lock = new();
    private readonly List<StartupPhase> _phases;

    public StartupProgressService(
        ILogger<StartupProgressService> logger,
        ILoggerFactory loggerFactory,
        IHubContext<PlayerStatusHub> hubContext)
    {
        _logger = logger;
        _triggerLogger = loggerFactory.CreateLogger("TriggerStartup");
        _hubContext = hubContext;

        _phases = new List<StartupPhase>
        {
            new("profiles", "Restoring sound card profiles"),
            new("sinks", "Loading custom audio sinks"),
            new("devices", "Detecting audio devices"),
            new("players", "Starting audio players"),
            new("triggers", "Initializing 12V triggers"),
            new("hidbuttons", "Initializing HID buttons"),
            new("mqtt", "Connecting MQTT bridge")
        };
    }

    /// <summary>
    /// Whether all startup phases have completed (or failed).
    /// </summary>
    public bool IsStartupComplete
    {
        get
        {
            lock (_lock)
            {
                return _phases.All(p =>
                    p.Status == StartupPhaseStatus.Completed ||
                    p.Status == StartupPhaseStatus.Failed);
            }
        }
    }

    /// <summary>
    /// Updates a startup phase and broadcasts the change to all connected clients.
    /// </summary>
    /// <param name="phaseId">The phase identifier (e.g., "profiles", "sinks").</param>
    /// <param name="status">The new status.</param>
    /// <param name="detail">Optional detail text (e.g., "4 players started").</param>
    public void SetPhase(string phaseId, StartupPhaseStatus status, string? detail = null)
    {
        StartupProgressResponse snapshot;

        lock (_lock)
        {
            var phase = _phases.FirstOrDefault(p => p.Id == phaseId);
            if (phase == null)
            {
                _logger.LogWarning("Unknown startup phase: {PhaseId}", phaseId);
                return;
            }

            phase.Status = status;
            if (detail != null)
                phase.Detail = detail;

            snapshot = BuildSnapshot();
        }

        // Use trigger-specific logger for trigger and hidbuttons phases
        var logger = (phaseId == "triggers" || phaseId == "hidbuttons") ? _triggerLogger : _logger;
        var phaseName = snapshot.Phases.First(p => p.Id == phaseId).Name;

        if (status == StartupPhaseStatus.InProgress)
        {
            logger.LogInformation("Startup phase: {PhaseName}...", phaseName);
        }
        else if (status == StartupPhaseStatus.Completed)
        {
            logger.LogInformation("Startup phase: {PhaseName} complete{Detail}",
                phaseName,
                detail != null ? $" ({detail})" : "");
        }
        else if (status == StartupPhaseStatus.Failed)
        {
            logger.LogWarning("Startup phase: {PhaseName} failed{Detail}",
                phaseName,
                detail != null ? $" ({detail})" : "");
        }

        // Fire-and-forget broadcast — never delays the caller
        _ = BroadcastAsync(snapshot);
    }

    /// <summary>
    /// Returns the current startup progress snapshot for the HTTP endpoint.
    /// </summary>
    public StartupProgressResponse GetProgress()
    {
        lock (_lock)
        {
            return BuildSnapshot();
        }
    }

    private StartupProgressResponse BuildSnapshot()
    {
        return new StartupProgressResponse(
            IsStartupComplete,
            _phases.Select(p => new StartupPhaseResponse(p.Id, p.Name, p.Status, p.Detail)).ToList()
        );
    }

    private async Task BroadcastAsync(StartupProgressResponse snapshot)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("StartupProgress", snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast startup progress (clients may not be connected yet)");
        }
    }

    /// <summary>
    /// Internal mutable phase state.
    /// </summary>
    private class StartupPhase
    {
        public string Id { get; }
        public string Name { get; }
        public StartupPhaseStatus Status { get; set; } = StartupPhaseStatus.Pending;
        public string? Detail { get; set; }

        public StartupPhase(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}

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

/// <summary>
/// Response model for a single startup phase.
/// </summary>
public record StartupPhaseResponse(
    string Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StartupPhaseStatus Status,
    string? Detail
);

/// <summary>
/// Response model for the startup progress endpoint.
/// </summary>
public record StartupProgressResponse(
    bool Complete,
    List<StartupPhaseResponse> Phases
);
