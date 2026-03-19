namespace MultiRoomAudio.Exceptions;

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
