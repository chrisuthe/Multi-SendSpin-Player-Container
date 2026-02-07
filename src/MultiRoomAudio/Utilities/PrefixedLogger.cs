using Microsoft.Extensions.Logging;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// A logger wrapper that adds a prefix to all log messages.
/// Used to add player context to SDK log messages.
/// </summary>
/// <typeparam name="T">The category type (typically the SDK class being wrapped).</typeparam>
public sealed class PrefixedLogger<T> : ILogger<T>
{
    private readonly ILogger<T> _inner;
    private readonly string _prefix;

    /// <summary>
    /// Creates a new prefixed logger.
    /// </summary>
    /// <param name="inner">The underlying logger to wrap.</param>
    /// <param name="prefix">The prefix to add to all messages (e.g., "[Study] ").</param>
    public PrefixedLogger(ILogger<T> inner, string prefix)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        // Wrap the formatter to add the prefix
        _inner.Log(
            logLevel,
            eventId,
            state,
            exception,
            (s, ex) => _prefix + formatter(s, ex));
    }
}

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
