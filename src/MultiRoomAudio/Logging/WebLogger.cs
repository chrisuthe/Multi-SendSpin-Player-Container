using MultiRoomAudio.Services.Logging;

namespace MultiRoomAudio.Logging;

/// <summary>
/// Logger implementation that writes to LoggingService.
/// </summary>
public class WebLogger : ILogger
{
    private readonly string _categoryName;
    private readonly LoggingService _loggingService;
    private readonly LogLevel _minLevel;
    private readonly LogCategory _category;

    public WebLogger(string categoryName, LoggingService loggingService, LogLevel minLevel)
    {
        _categoryName = categoryName;
        _loggingService = loggingService;
        _minLevel = minLevel;
        _category = LoggingService.DetectCategory(categoryName);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _minLevel && logLevel != LogLevel.None;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null)
            return;

        var exceptionText = exception?.ToString();

        var entry = new LogEntry(
            DateTime.UtcNow,
            logLevel,
            _category,
            message,
            exceptionText
        );

        _loggingService.AddEntry(entry);
    }

    /// <summary>
    /// Null scope implementation for logging scopes.
    /// </summary>
    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
