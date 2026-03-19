namespace MultiRoomAudio.Utilities;

/// <summary>
/// Extension methods for creating prefixed loggers.
/// </summary>
public static class PrefixedLoggerExtensions
{
    /// <summary>
    /// Creates a logger with a player name prefix for easier debugging.
    /// </summary>
    /// <typeparam name="T">The logger category type.</typeparam>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="playerName">The player name to use as prefix.</param>
    /// <returns>A logger that prefixes all messages with "[playerName] ".</returns>
    public static ILogger<T> CreatePlayerLogger<T>(
        this ILoggerFactory loggerFactory,
        string playerName)
    {
        var inner = loggerFactory.CreateLogger<T>();
        return new PrefixedLogger<T>(inner, $"[{playerName}] ");
    }
}
