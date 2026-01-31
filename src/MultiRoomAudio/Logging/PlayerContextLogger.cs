namespace MultiRoomAudio.Logging;

/// <summary>
/// A logger wrapper that prepends player name to all log messages.
/// Used to add player context to SDK log messages (e.g., "Re-anchoring required" from AudioPipeline).
/// </summary>
/// <remarks>
/// SDK components like AudioPipeline, TimedAudioBuffer, and SendspinConnection emit log messages
/// without knowing which player they belong to. This wrapper adds that context by prepending
/// [PlayerName] to every message, making it easy to identify which player is affected.
/// </remarks>
/// <typeparam name="T">The category type for the underlying logger.</typeparam>
public class PlayerContextLogger<T> : ILogger<T>
{
    private readonly ILogger<T> _inner;
    private readonly string _playerName;

    /// <summary>
    /// Creates a new player context logger.
    /// </summary>
    /// <param name="inner">The underlying logger to wrap.</param>
    /// <param name="playerName">The player name to prepend to all messages.</param>
    public PlayerContextLogger(ILogger<T> inner, string playerName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _playerName = playerName ?? throw new ArgumentNullException(nameof(playerName));
    }

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        // Prepend player name to the formatted message
        var originalMessage = formatter(state, exception);
        var prefixedMessage = $"[{_playerName}] {originalMessage}";

        // Log with the prefixed message
        _inner.Log(logLevel, eventId, prefixedMessage, exception, (s, e) => s);
    }

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _inner.BeginScope(state);
}

/// <summary>
/// Non-generic version of PlayerContextLogger for loggers without a category type.
/// </summary>
public class PlayerContextLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly string _playerName;

    /// <summary>
    /// Creates a new player context logger.
    /// </summary>
    /// <param name="inner">The underlying logger to wrap.</param>
    /// <param name="playerName">The player name to prepend to all messages.</param>
    public PlayerContextLogger(ILogger inner, string playerName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _playerName = playerName ?? throw new ArgumentNullException(nameof(playerName));
    }

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var originalMessage = formatter(state, exception);
        var prefixedMessage = $"[{_playerName}] {originalMessage}";

        _inner.Log(logLevel, eventId, prefixedMessage, exception, (s, e) => s);
    }

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _inner.BeginScope(state);
}
