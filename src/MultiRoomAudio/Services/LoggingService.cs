using System.Collections.Concurrent;
using System.Text;

namespace MultiRoomAudio.Services;

/// <summary>
/// Log category for filtering and organization.
/// </summary>
public enum LogCategory
{
    System,     // Startup, shutdown, environment
    Player,     // Player create, connect, play, stop, delete
    Audio,      // PulseAudio, volume control, underflows
    API,        // HTTP requests/responses
    Config,     // Configuration load/save/CRUD
    SDK         // SDK interactions, connection state, sync
}

/// <summary>
/// Represents a single log entry with timestamp and metadata.
/// </summary>
public record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    LogCategory Category,
    string Message,
    string? Exception = null
);

/// <summary>
/// Options for querying logs.
/// </summary>
public record LogQueryOptions(
    LogLevel? MinLevel = null,
    LogCategory? Category = null,
    string? SearchText = null,
    DateTime? StartTime = null,
    DateTime? EndTime = null,
    int Skip = 0,
    int Take = 100,
    bool NewestFirst = true
);

/// <summary>
/// Manages log collection, file persistence, in-memory buffering, and retrieval.
/// </summary>
public class LoggingService : IDisposable
{
    private readonly EnvironmentService _environment;
    private readonly CircularBuffer<LogEntry> _buffer;
    private readonly object _fileLock = new();
    private readonly object _bufferLock = new();
    private StreamWriter? _fileWriter;
    private string? _currentLogFilePath;
    private bool _disposed;

    private const int InMemoryBufferSize = 2000;
    private const long MaxLogFileSizeBytes = 10 * 1024 * 1024; // 10MB
    private const int MaxLogFileCount = 5;
    private const string LogFileName = "multiroom-audio.log";

    /// <summary>
    /// Event fired when a new log entry is added.
    /// </summary>
    public event EventHandler<LogEntry>? LogEntryAdded;

    public LoggingService(EnvironmentService environment)
    {
        _environment = environment;
        _buffer = new CircularBuffer<LogEntry>(InMemoryBufferSize);

        InitializeFileLogging();
    }

    private void InitializeFileLogging()
    {
        try
        {
            var logDir = _environment.LogPath;
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            _currentLogFilePath = Path.Combine(logDir, LogFileName);
            _fileWriter = new StreamWriter(_currentLogFilePath, append: true, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch (Exception)
        {
            // File logging initialization failed - continue with in-memory only
            _fileWriter = null;
        }
    }

    /// <summary>
    /// Adds a log entry to the buffer and file.
    /// </summary>
    public void AddEntry(LogEntry entry)
    {
        if (_disposed) return;

        // Add to in-memory buffer
        lock (_bufferLock)
        {
            _buffer.Add(entry);
        }

        // Write to file
        WriteToFile(entry);

        // Fire event for real-time streaming
        LogEntryAdded?.Invoke(this, entry);
    }

    /// <summary>
    /// Adds a log entry with auto-detected category.
    /// </summary>
    public void AddEntry(LogLevel level, string categoryName, string message, string? exception = null)
    {
        var category = DetectCategory(categoryName);
        var entry = new LogEntry(DateTime.UtcNow, level, category, message, exception);
        AddEntry(entry);
    }

    /// <summary>
    /// Gets log entries matching the specified query options.
    /// </summary>
    public IReadOnlyList<LogEntry> GetEntries(LogQueryOptions? options = null)
    {
        options ??= new LogQueryOptions();

        lock (_bufferLock)
        {
            var query = _buffer.AsEnumerable();

            // Apply filters
            if (options.MinLevel.HasValue)
            {
                query = query.Where(e => e.Level >= options.MinLevel.Value);
            }

            if (options.Category.HasValue)
            {
                query = query.Where(e => e.Category == options.Category.Value);
            }

            if (!string.IsNullOrWhiteSpace(options.SearchText))
            {
                var search = options.SearchText.ToLowerInvariant();
                query = query.Where(e =>
                    e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (e.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (options.StartTime.HasValue)
            {
                query = query.Where(e => e.Timestamp >= options.StartTime.Value);
            }

            if (options.EndTime.HasValue)
            {
                query = query.Where(e => e.Timestamp <= options.EndTime.Value);
            }

            // Order
            query = options.NewestFirst
                ? query.OrderByDescending(e => e.Timestamp)
                : query.OrderBy(e => e.Timestamp);

            // Pagination
            return query.Skip(options.Skip).Take(options.Take).ToList();
        }
    }

    /// <summary>
    /// Gets the total count of entries matching the filter (without pagination).
    /// </summary>
    public int GetTotalCount(LogQueryOptions? options = null)
    {
        options ??= new LogQueryOptions();

        lock (_bufferLock)
        {
            var query = _buffer.AsEnumerable();

            if (options.MinLevel.HasValue)
            {
                query = query.Where(e => e.Level >= options.MinLevel.Value);
            }

            if (options.Category.HasValue)
            {
                query = query.Where(e => e.Category == options.Category.Value);
            }

            if (!string.IsNullOrWhiteSpace(options.SearchText))
            {
                var search = options.SearchText.ToLowerInvariant();
                query = query.Where(e =>
                    e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (e.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (options.StartTime.HasValue)
            {
                query = query.Where(e => e.Timestamp >= options.StartTime.Value);
            }

            if (options.EndTime.HasValue)
            {
                query = query.Where(e => e.Timestamp <= options.EndTime.Value);
            }

            return query.Count();
        }
    }

    /// <summary>
    /// Gets statistics about the current logs.
    /// </summary>
    public LogStats GetStats()
    {
        lock (_bufferLock)
        {
            var entries = _buffer.ToList();

            var byLevel = entries
                .GroupBy(e => e.Level)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            var byCategory = entries
                .GroupBy(e => e.Category)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            return new LogStats(
                byLevel,
                byCategory,
                entries.Count,
                entries.MinBy(e => e.Timestamp)?.Timestamp,
                entries.MaxBy(e => e.Timestamp)?.Timestamp
            );
        }
    }

    /// <summary>
    /// Clears all logs from memory and deletes log files.
    /// </summary>
    public void Clear()
    {
        lock (_bufferLock)
        {
            _buffer.Clear();
        }

        lock (_fileLock)
        {
            try
            {
                _fileWriter?.Dispose();
                _fileWriter = null;

                // Delete all log files
                var logDir = _environment.LogPath;
                if (Directory.Exists(logDir))
                {
                    var logFiles = Directory.GetFiles(logDir, "multiroom-audio*.log");
                    foreach (var file in logFiles)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Ignore individual file deletion errors
                        }
                    }
                }

                // Reinitialize file logging
                InitializeFileLogging();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private void WriteToFile(LogEntry entry)
    {
        lock (_fileLock)
        {
            if (_fileWriter == null || _currentLogFilePath == null) return;

            try
            {
                // Check for rotation
                RotateLogFileIfNeeded();

                // Format: 2026-01-10T14:23:45.123Z|INFO|Player|Message|Exception
                var line = FormatLogLine(entry);
                _fileWriter.WriteLine(line);
            }
            catch
            {
                // Ignore file write errors
            }
        }
    }

    private static string FormatLogLine(LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Timestamp.ToString("o"));
        sb.Append('|');
        sb.Append(entry.Level.ToString().ToUpperInvariant());
        sb.Append('|');
        sb.Append(entry.Category);
        sb.Append('|');
        sb.Append(entry.Message.Replace('\n', ' ').Replace('\r', ' '));

        if (!string.IsNullOrEmpty(entry.Exception))
        {
            sb.Append('|');
            sb.Append(entry.Exception.Replace('\n', ' ').Replace('\r', ' '));
        }

        return sb.ToString();
    }

    private void RotateLogFileIfNeeded()
    {
        if (_currentLogFilePath == null || !File.Exists(_currentLogFilePath)) return;

        var fileInfo = new FileInfo(_currentLogFilePath);
        if (fileInfo.Length < MaxLogFileSizeBytes) return;

        try
        {
            _fileWriter?.Dispose();
            _fileWriter = null;

            var logDir = _environment.LogPath;

            // Shift existing files: 4->delete, 3->4, 2->3, 1->2, current->1
            for (int i = MaxLogFileCount - 1; i >= 1; i--)
            {
                var oldPath = Path.Combine(logDir, $"multiroom-audio.{i}.log");
                var newPath = Path.Combine(logDir, $"multiroom-audio.{i + 1}.log");

                if (File.Exists(newPath))
                {
                    File.Delete(newPath);
                }
                if (File.Exists(oldPath))
                {
                    File.Move(oldPath, newPath);
                }
            }

            // Move current to .1
            var rotatedPath = Path.Combine(logDir, "multiroom-audio.1.log");
            File.Move(_currentLogFilePath, rotatedPath);

            // Create new current file
            _fileWriter = new StreamWriter(_currentLogFilePath, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch
        {
            // Try to reinitialize file logging
            InitializeFileLogging();
        }
    }

    /// <summary>
    /// Determines the log category based on the logger category name.
    /// </summary>
    public static LogCategory DetectCategory(string categoryName)
    {
        if (string.IsNullOrEmpty(categoryName))
            return LogCategory.System;

        return categoryName switch
        {
            var c when c.Contains("PlayerManager", StringComparison.OrdinalIgnoreCase) => LogCategory.Player,
            var c when c.Contains("PlayerStatus", StringComparison.OrdinalIgnoreCase) => LogCategory.Player,
            var c when c.Contains("Audio", StringComparison.OrdinalIgnoreCase) => LogCategory.Audio,
            var c when c.Contains("Pulse", StringComparison.OrdinalIgnoreCase) => LogCategory.Audio,
            var c when c.Contains("Volume", StringComparison.OrdinalIgnoreCase) => LogCategory.Audio,
            var c when c.Contains("Endpoint", StringComparison.OrdinalIgnoreCase) => LogCategory.API,
            var c when c.Contains("Controller", StringComparison.OrdinalIgnoreCase) => LogCategory.API,
            var c when c.Contains("Configuration", StringComparison.OrdinalIgnoreCase) => LogCategory.Config,
            var c when c.Contains("Config", StringComparison.OrdinalIgnoreCase) => LogCategory.Config,
            var c when c.Contains("SDK", StringComparison.OrdinalIgnoreCase) => LogCategory.SDK,
            var c when c.Contains("Sendspin", StringComparison.OrdinalIgnoreCase) => LogCategory.SDK,
            var c when c.Contains("Connection", StringComparison.OrdinalIgnoreCase) => LogCategory.SDK,
            var c when c.Contains("Environment", StringComparison.OrdinalIgnoreCase) => LogCategory.System,
            var c when c.StartsWith("Program", StringComparison.OrdinalIgnoreCase) => LogCategory.System,
            _ => LogCategory.System
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_fileLock)
        {
            _fileWriter?.Dispose();
            _fileWriter = null;
        }
    }
}

/// <summary>
/// Statistics about the current logs.
/// </summary>
public record LogStats(
    Dictionary<string, int> ByLevel,
    Dictionary<string, int> ByCategory,
    int TotalEntries,
    DateTime? OldestEntry,
    DateTime? NewestEntry
);

/// <summary>
/// A simple fixed-size circular buffer.
/// </summary>
public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _start;
    private int _count;

    public CircularBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new T[capacity];
        _start = 0;
        _count = 0;
    }

    public int Count => _count;

    public void Add(T item)
    {
        var index = (_start + _count) % _capacity;
        _buffer[index] = item;

        if (_count < _capacity)
        {
            _count++;
        }
        else
        {
            // Buffer is full, overwrite oldest
            _start = (_start + 1) % _capacity;
        }
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
        Array.Clear(_buffer);
    }

    public IEnumerable<T> AsEnumerable()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return _buffer[(_start + i) % _capacity];
        }
    }

    public List<T> ToList()
    {
        var list = new List<T>(_count);
        for (int i = 0; i < _count; i++)
        {
            list.Add(_buffer[(_start + i) % _capacity]);
        }
        return list;
    }
}
