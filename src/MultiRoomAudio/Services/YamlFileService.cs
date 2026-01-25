using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services;

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

/// <summary>
/// Base class for services that persist dictionary data to YAML files.
/// Provides thread-safe CRUD operations with automatic persistence.
/// </summary>
/// <typeparam name="TKey">The dictionary key type (typically string).</typeparam>
/// <typeparam name="TValue">The dictionary value type.</typeparam>
public abstract class YamlDictionaryService<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private Dictionary<TKey, TValue> _data = new();

    /// <summary>
    /// Creates a new YAML dictionary service.
    /// </summary>
    /// <param name="filePath">Full path to the YAML file.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    protected YamlDictionaryService(string filePath, ILogger logger)
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
    /// Lock object for thread-safe operations.
    /// For write operations, use Lock.EnterWriteLock()/ExitWriteLock().
    /// For read operations, use Lock.EnterReadLock()/ExitReadLock().
    /// </summary>
    protected ReaderWriterLockSlim Lock => _lock;

    /// <summary>
    /// Number of items in the dictionary.
    /// </summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _data.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Load data from the YAML file.
    /// </summary>
    /// <returns>True if data was loaded, false if file doesn't exist or is empty.</returns>
    public virtual bool Load()
    {
        // Read file content outside the lock
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
                _data = new Dictionary<TKey, TValue>();
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
                _data = new Dictionary<TKey, TValue>();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return false;
        }

        // Process the data under write lock
        _lock.EnterWriteLock();
        try
        {
            if (!fileExists)
            {
                _logger.LogDebug("YAML file not found at {Path}, starting with empty dictionary", _filePath);
                _data = new Dictionary<TKey, TValue>();
                return false;
            }

            if (string.IsNullOrWhiteSpace(yaml))
            {
                _logger.LogDebug("YAML file {Path} is empty, starting with empty dictionary", _filePath);
                _data = new Dictionary<TKey, TValue>();
                return false;
            }

            _data = _deserializer.Deserialize<Dictionary<TKey, TValue>>(yaml)
                ?? new Dictionary<TKey, TValue>();

            OnDataLoaded(_data);
            _logger.LogDebug("Loaded {Count} items from {Path}", _data.Count, _filePath);
            return true;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            _logger.LogError(ex, "Failed to parse YAML from {Path}. File may be malformed", _filePath);
            _data = new Dictionary<TKey, TValue>();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error parsing from {Path}", _filePath);
            _data = new Dictionary<TKey, TValue>();
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
        // Serialize under read lock
        string yaml;
        int count;
        _lock.EnterReadLock();
        try
        {
            yaml = _serializer.Serialize(_data);
            count = _data.Count;
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
            _logger.LogDebug("Saved {Count} items to {Path}", count, _filePath);
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
    /// Get a value by key.
    /// </summary>
    public TValue? Get(TKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _data.TryGetValue(key, out var value) ? value : default;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Try to get a value by key.
    /// </summary>
    public bool TryGet(TKey key, out TValue? value)
    {
        _lock.EnterReadLock();
        try
        {
            if (_data.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }
            value = default;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Set a value by key and optionally save.
    /// </summary>
    public bool Set(TKey key, TValue value, bool save = true)
    {
        _lock.EnterWriteLock();
        try
        {
            _data[key] = value;
            OnItemSet(key, value);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // Save outside the lock
        return save ? Save() : true;
    }

    /// <summary>
    /// Remove a value by key and optionally save.
    /// </summary>
    public bool Remove(TKey key, bool save = true)
    {
        bool removed;
        _lock.EnterWriteLock();
        try
        {
            removed = _data.Remove(key);
            if (removed)
            {
                OnItemRemoved(key);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (!removed)
            return false;

        // Save outside the lock
        return save ? Save() : true;
    }

    /// <summary>
    /// Check if a key exists.
    /// </summary>
    public bool ContainsKey(TKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _data.ContainsKey(key);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get all keys.
    /// </summary>
    public IReadOnlyList<TKey> GetKeys()
    {
        _lock.EnterReadLock();
        try
        {
            return _data.Keys.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get all values.
    /// </summary>
    public IReadOnlyList<TValue> GetValues()
    {
        _lock.EnterReadLock();
        try
        {
            return _data.Values.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get a snapshot of all data as a read-only dictionary.
    /// </summary>
    public IReadOnlyDictionary<TKey, TValue> GetAll()
    {
        _lock.EnterReadLock();
        try
        {
            return new Dictionary<TKey, TValue>(_data);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Update an existing item with a function, then optionally save.
    /// </summary>
    public bool Update(TKey key, Action<TValue> updateAction, bool save = true)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_data.TryGetValue(key, out var value))
                return false;

            updateAction(value);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // Save outside the lock
        return save ? Save() : true;
    }

    /// <summary>
    /// Get or create a value with a factory function.
    /// </summary>
    public TValue GetOrCreate(TKey key, Func<TValue> factory, bool save = true)
    {
        // First try with read lock
        _lock.EnterReadLock();
        try
        {
            if (_data.TryGetValue(key, out var existing))
                return existing;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Need to create - use write lock
        TValue value;
        bool needsSave = false;
        _lock.EnterWriteLock();
        try
        {
            // Double-check in case another thread added it
            if (_data.TryGetValue(key, out var existing))
                return existing;

            value = factory();
            _data[key] = value;
            OnItemSet(key, value);
            needsSave = save;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // Save outside the lock
        if (needsSave)
            Save();

        return value;
    }

    /// <summary>
    /// Execute an action within the read lock for complex read operations.
    /// </summary>
    protected TResult WithReadLock<TResult>(Func<Dictionary<TKey, TValue>, TResult> action)
    {
        _lock.EnterReadLock();
        try
        {
            return action(_data);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Execute an action within the write lock for complex write operations.
    /// </summary>
    protected TResult WithWriteLock<TResult>(Func<Dictionary<TKey, TValue>, TResult> action)
    {
        _lock.EnterWriteLock();
        try
        {
            return action(_data);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Execute an action within the write lock for complex write operations.
    /// </summary>
    protected void WithWriteLock(Action<Dictionary<TKey, TValue>> action)
    {
        _lock.EnterWriteLock();
        try
        {
            action(_data);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Called after data is successfully loaded. Override to perform post-load processing
    /// like ensuring keys match value fields.
    /// Called within the write lock.
    /// </summary>
    protected virtual void OnDataLoaded(Dictionary<TKey, TValue> data)
    {
    }

    /// <summary>
    /// Called after an item is set. Override for logging or validation.
    /// Called within the write lock.
    /// </summary>
    protected virtual void OnItemSet(TKey key, TValue value)
    {
    }

    /// <summary>
    /// Called after an item is removed. Override for logging.
    /// Called within the lock.
    /// </summary>
    protected virtual void OnItemRemoved(TKey key)
    {
    }
}
