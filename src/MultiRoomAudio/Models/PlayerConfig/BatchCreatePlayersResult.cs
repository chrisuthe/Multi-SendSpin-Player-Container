namespace MultiRoomAudio.Models.PlayerConfig;

/// <summary>
/// Result of a batch player creation operation.
/// </summary>
public record BatchCreatePlayersResult(
    List<string> Created,
    List<string> Started,
    List<BatchPlayerFailure> Failed)
{
    /// <summary>
    /// Whether all requested players were created successfully.
    /// </summary>
    public bool Success => Failed.Count == 0;

    /// <summary>
    /// The total number of players that were created (saved to config).
    /// </summary>
    public int CreatedCount => Created.Count;

    /// <summary>
    /// The total number of players that were started successfully.
    /// </summary>
    public int StartedCount => Started.Count;

    /// <summary>
    /// The total number of players that failed to create.
    /// </summary>
    public int FailedCount => Failed.Count;
}
