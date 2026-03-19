using System.Collections.Concurrent;
using MultiRoomAudio.Services.Logging;

namespace MultiRoomAudio.Logging;

/// <summary>
/// Custom logger provider that routes logs to LoggingService.
/// </summary>
public class WebLoggingProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, WebLogger> _loggers = new();
    private readonly LoggingService _loggingService;
    private readonly LogLevel _minLevel;
    private bool _disposed;

    public WebLoggingProvider(LoggingService loggingService, LogLevel minLevel)
    {
        _loggingService = loggingService;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name =>
            new WebLogger(name, _loggingService, _minLevel));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loggers.Clear();
    }
}
