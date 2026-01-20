namespace MultiRoomAudio.Exceptions;

/// <summary>
/// Thrown when an entity (player, sink) already exists.
/// Maps to HTTP 409 Conflict.
/// </summary>
public class EntityAlreadyExistsException : InvalidOperationException
{
    /// <summary>
    /// The type of entity that already exists (e.g., "Player", "Sink").
    /// </summary>
    public string EntityType { get; }

    /// <summary>
    /// The name/identifier of the entity that already exists.
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// Creates a new EntityAlreadyExistsException.
    /// </summary>
    /// <param name="entityType">The type of entity (e.g., "Player", "Sink").</param>
    /// <param name="entityName">The name of the entity that already exists.</param>
    public EntityAlreadyExistsException(string entityType, string entityName)
        : base($"{entityType} '{entityName}' already exists")
    {
        EntityType = entityType;
        EntityName = entityName;
    }
}

/// <summary>
/// Thrown when an operation is already in progress (e.g., test tone already playing).
/// Maps to HTTP 409 Conflict.
/// </summary>
public class OperationInProgressException : InvalidOperationException
{
    /// <summary>
    /// The name of the operation that is already in progress.
    /// </summary>
    public string OperationName { get; }

    /// <summary>
    /// Creates a new OperationInProgressException.
    /// </summary>
    /// <param name="operationName">The name of the operation already in progress.</param>
    public OperationInProgressException(string operationName)
        : base($"{operationName} is already in progress")
    {
        OperationName = operationName;
    }
}
