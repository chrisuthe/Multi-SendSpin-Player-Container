using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services.YamlFile;

/// <summary>
/// Base class for services that persist data to YAML files.
/// Provides thread-safe load/save operations with standard serialization settings.
/// </summary>
/// <typeparam name="T">The type of data to persist.</typeparam>
public abstract class YamlFileService<T> where T : class, new()
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    protected T Data { get; private set; } = new();

    /// <summary>
    /// Creates a new YAML file service.
    /// </summary>
    /// <param name="filePath">Full path to the YAML file.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    protected YamlFileService(string filePath, ILogger logger)
    {
        _filePath = filePath;
        _logger = logger;

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <summary>
    /// Path to the YAML file.
    /// </summary>
    protected string FilePath => _filePath;

    /// <summary>
    /// Logger instance for derived classes.
    /// </summary>
    protected ILogger Logger => _logger;

    /// <summary>
    /// Lock object for thread-safe operations. Use this when accessing Data directly.
    /// For write operations, use Lock.EnterWriteLock()/ExitWriteLock().
    /// For read operations, use Lock.EnterReadLock()/ExitReadLock().
    /// </summary>
    protected ReaderWriterLockSlim Lock => _lock;

    /// <summary>
    /// Load data from the YAML file.
    /// </summary>
    /// <returns>True if data was loaded, false if file doesn't exist or is empty.</returns>
    public virtual bool Load()
    {
        // Read file content outside the lock to avoid blocking on slow I/O
        string? yaml = null;
        bool fileExists;

        try
        {
            fileExists = File.Exists(_filePath);
            if (fileExists)
            {
                yaml = File.ReadAllText(_filePath);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to read {Path}. Check file permissions", _filePath);
            _lock.EnterWriteLock();
            try
            {
                Data = new T();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading from {Path}", _filePath);
            _lock.EnterWriteLock();
            try
            {
                Data = new T();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return false;
        }

        // Process the data under write lock (updating Data)
        _lock.EnterWriteLock();
        try
        {
            if (!fileExists)
            {
                _logger.LogDebug("YAML file not found at {Path}, starting with defaults", _filePath);
                Data = new T();
                return false;
            }

            if (string.IsNullOrWhiteSpace(yaml))
            {
                _logger.LogDebug("YAML file {Path} is empty, starting with defaults", _filePath);
                Data = new T();
                return false;
            }

            Data = _deserializer.Deserialize<T>(yaml) ?? new T();
            OnDataLoaded();
            _logger.LogDebug("Loaded data from {Path}", _filePath);
            return true;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            _logger.LogError(ex, "Failed to parse YAML from {Path}. File may be malformed", _filePath);
            Data = new T();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error parsing from {Path}", _filePath);
            Data = new T();
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Save data to the YAML file.
    /// </summary>
    /// <returns>True if saved successfully.</returns>
    public virtual bool Save()
    {
        // Serialize under read lock (we're only reading Data)
        string yaml;
        _lock.EnterReadLock();
        try
        {
            yaml = _serializer.Serialize(Data);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Write file outside the lock
        try
        {
            // Ensure directory exists
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_filePath, yaml);
            _logger.LogDebug("Saved data to {Path}", _filePath);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to write {Path}. Check disk space and permissions", _filePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error saving to {Path}", _filePath);
            return false;
        }
    }

    /// <summary>
    /// Called after data is successfully loaded. Override to perform post-load processing.
    /// Called within the write lock, so thread-safe access to Data is guaranteed.
    /// </summary>
    protected virtual void OnDataLoaded()
    {
    }
}
