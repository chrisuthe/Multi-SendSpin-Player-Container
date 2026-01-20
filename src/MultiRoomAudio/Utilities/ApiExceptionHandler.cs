using Microsoft.Extensions.Logging;
using MultiRoomAudio.Exceptions;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// Provides standardized exception handling for API endpoints.
/// Reduces duplication of try-catch patterns across controllers.
/// </summary>
public static class ApiExceptionHandler
{
    /// <summary>
    /// Wraps an async operation with standardized exception handling.
    /// </summary>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="logger">Logger for error logging.</param>
    /// <param name="operationName">Name of the operation for error messages (e.g., "create player").</param>
    /// <param name="entityName">Optional entity name for logging context.</param>
    /// <returns>The result from the operation or an appropriate error response.</returns>
    public static async Task<IResult> ExecuteAsync(
        Func<Task<IResult>> operation,
        ILogger logger,
        string operationName,
        string? entityName = null)
    {
        try
        {
            return await operation();
        }
        catch (EntityAlreadyExistsException ex)
        {
            LogWarning(logger, "conflict", operationName, entityName, ex.Message);
            return Results.Conflict(new ErrorResponse(false, ex.Message));
        }
        catch (OperationInProgressException ex)
        {
            // Already logged at DEBUG level upstream (e.g., ToneGeneratorService)
            return Results.Conflict(new ErrorResponse(false, ex.Message));
        }
        catch (ArgumentException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            LogWarning(logger, "not found", operationName, entityName, ex.Message);
            return Results.NotFound(new ErrorResponse(false, ex.Message));
        }
        catch (ArgumentException ex)
        {
            LogWarning(logger, "bad request", operationName, entityName, ex.Message);
            return Results.BadRequest(new ErrorResponse(false, ex.Message));
        }
        catch (TimeoutException ex)
        {
            LogWarning(logger, "timeout", operationName, entityName, ex.Message);
            return Results.Problem(
                detail: ex.Message,
                statusCode: 408,
                title: $"Timeout during {operationName}");
        }
        catch (Exception ex)
        {
            LogError(logger, operationName, entityName, ex);
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: $"Failed to {operationName}");
        }
    }

    /// <summary>
    /// Wraps a synchronous operation with standardized exception handling.
    /// </summary>
    public static IResult Execute(
        Func<IResult> operation,
        ILogger logger,
        string operationName,
        string? entityName = null)
    {
        try
        {
            return operation();
        }
        catch (EntityAlreadyExistsException ex)
        {
            LogWarning(logger, "conflict", operationName, entityName, ex.Message);
            return Results.Conflict(new ErrorResponse(false, ex.Message));
        }
        catch (OperationInProgressException ex)
        {
            // Already logged at DEBUG level upstream (e.g., ToneGeneratorService)
            return Results.Conflict(new ErrorResponse(false, ex.Message));
        }
        catch (ArgumentException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            LogWarning(logger, "not found", operationName, entityName, ex.Message);
            return Results.NotFound(new ErrorResponse(false, ex.Message));
        }
        catch (ArgumentException ex)
        {
            LogWarning(logger, "bad request", operationName, entityName, ex.Message);
            return Results.BadRequest(new ErrorResponse(false, ex.Message));
        }
        catch (TimeoutException ex)
        {
            LogWarning(logger, "timeout", operationName, entityName, ex.Message);
            return Results.Problem(
                detail: ex.Message,
                statusCode: 408,
                title: $"Timeout during {operationName}");
        }
        catch (Exception ex)
        {
            LogError(logger, operationName, entityName, ex);
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: $"Failed to {operationName}");
        }
    }

    private static void LogWarning(ILogger logger, string errorType, string operation, string? entityName, string message)
    {
        if (entityName != null)
            logger.LogWarning("API: {Operation} {ErrorType} for {Entity} - {Message}", operation, errorType, entityName, message);
        else
            logger.LogWarning("API: {Operation} {ErrorType} - {Message}", operation, errorType, message);
    }

    private static void LogError(ILogger logger, string operation, string? entityName, Exception ex)
    {
        if (entityName != null)
            logger.LogError(ex, "API: Failed to {Operation} for {Entity}", operation, entityName);
        else
            logger.LogError(ex, "API: Failed to {Operation}", operation);
    }
}
