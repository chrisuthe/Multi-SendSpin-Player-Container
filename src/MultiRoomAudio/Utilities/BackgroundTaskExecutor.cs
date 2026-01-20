using Microsoft.Extensions.Logging;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// Executes fire-and-forget tasks with proper exception logging.
/// Prevents unobserved task exceptions from crashing the application.
/// </summary>
public static class BackgroundTaskExecutor
{
    /// <summary>
    /// Safely executes a fire-and-forget task, ensuring any exceptions are logged.
    /// Use this when discarding a Task to prevent unobserved exceptions.
    /// </summary>
    /// <param name="task">The task to execute.</param>
    /// <param name="context">Description of what the task is doing (for logging).</param>
    /// <param name="logger">Logger to use for error reporting.</param>
    public static void FireAndForget(Task task, string context, ILogger logger)
    {
        task.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    logger.LogError(t.Exception.InnerException ?? t.Exception,
                        "Unhandled exception in fire-and-forget task: {Context}", context);
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Fire-and-forget overload that accepts an async lambda.
    /// </summary>
    /// <param name="taskFactory">Factory that creates the task to execute.</param>
    /// <param name="context">Description of what the task is doing (for logging).</param>
    /// <param name="logger">Logger to use for error reporting.</param>
    public static void FireAndForget(Func<Task> taskFactory, string context, ILogger logger)
    {
        FireAndForget(Task.Run(taskFactory), context, logger);
    }
}
