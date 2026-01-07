using MultiRoomAudio.Models;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using static MultiRoomAudio.Audio.PulseAudio.PulseAudioNative;

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// IAudioPlayer implementation using direct PulseAudio pa_simple API.
/// Designed for both HAOS and Docker environments where PulseAudio is available.
/// </summary>
/// <remarks>
/// This player uses PulseAudio's simple synchronous API. Audio data is written
/// directly to the PulseAudio server on a dedicated thread.
/// </remarks>
public class PulseAudioPlayer : IAudioPlayer
{
    private readonly ILogger<PulseAudioPlayer> _logger;
    private readonly object _lock = new();

    private IntPtr _paHandle = IntPtr.Zero;
    private IAudioSampleSource? _sampleSource;
    private AudioFormat? _currentFormat;
    private string? _sinkName;
    private bool _disposed;

    private Thread? _playbackThread;
    private CancellationTokenSource? _playbackCts;
    private volatile bool _isPlaying;
    private volatile bool _isPaused;

    // Pre-allocated buffers for the playback thread
    private float[]? _sampleBuffer;
    private byte[]? _byteBuffer;

    // Output format configuration
    private AudioOutputFormat? _outputFormat;
    private SampleFormat _paFormat = SampleFormat.FLOAT32LE;
    private int _bytesPerSample = 4;
    private bool _needsBitDepthConversion;

    /// <summary>
    /// Buffer size in milliseconds. Larger values increase latency but reduce underruns.
    /// </summary>
    private const int BufferMs = 50;

    /// <summary>
    /// Frames per write operation. At 48kHz, 1024 frames = ~21ms of audio.
    /// </summary>
    private const int FramesPerWrite = 1024;

    /// <summary>
    /// Number of consecutive write failures before attempting reconnection.
    /// </summary>
    private const int MaxConsecutiveFailures = 5;

    /// <summary>
    /// Maximum reconnection attempts before giving up.
    /// </summary>
    private const int MaxReconnectAttempts = 10;

    /// <summary>
    /// Base delay between reconnection attempts (exponential backoff).
    /// </summary>
    private const int ReconnectBaseDelayMs = 500;

    /// <summary>
    /// Maximum delay between reconnection attempts.
    /// </summary>
    private const int ReconnectMaxDelayMs = 10000;

    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    private volatile float _volume = 1.0f;
    private volatile bool _isMuted;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    public int OutputLatencyMs { get; private set; }

    public event EventHandler<AudioPlayerState>? StateChanged;
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the PulseAudioPlayer.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="sinkName">
    /// Optional PulseAudio sink name. If null, uses the default sink.
    /// Can be a sink name like "alsa_output.usb-..." or sink index.
    /// </param>
    /// <param name="outputFormat">
    /// Optional output format configuration. If null, uses 32-bit float output.
    /// Specifies the sample rate and bit depth to send to PulseAudio.
    /// PulseAudio handles any necessary format conversion to the device.
    /// </param>
    public PulseAudioPlayer(ILogger<PulseAudioPlayer> logger, string? sinkName = null, AudioOutputFormat? outputFormat = null)
    {
        _logger = logger;
        _sinkName = sinkName;
        _outputFormat = outputFormat;

        // Determine PulseAudio format based on requested bit depth
        if (outputFormat != null)
        {
            (_paFormat, _bytesPerSample, _needsBitDepthConversion) = outputFormat.BitDepth switch
            {
                16 => (SampleFormat.S16LE, 2, true),
                24 => (SampleFormat.S24_32LE, 4, true),  // 24-bit in 32-bit container
                32 => (SampleFormat.FLOAT32LE, 4, false),
                _ => (SampleFormat.FLOAT32LE, 4, false)
            };
        }
    }

    /// <summary>
    /// Gets the configured output format, or null if using default.
    /// </summary>
    public AudioOutputFormat? OutputFormat => _outputFormat;

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        // If we're currently playing, stop first to clean up the playback thread.
        // This handles the case where the SDK calls InitializeAsync without calling Stop()
        // first (e.g., when switching tracks).
        if (_isPlaying)
        {
            _logger.LogDebug("Stopping active playback before re-initialization");
            Stop();
        }

        lock (_lock)
        {
            try
            {
                // Free any existing handle first (handles track switching where SDK reuses the player).
                // Stop() above doesn't free the handle - only Flush() is called.
                // We must free it here to avoid resource leaks.
                if (_paHandle != IntPtr.Zero)
                {
                    _logger.LogDebug("Freeing existing PulseAudio handle before re-initialization");
                    try
                    {
                        SimpleFree(_paHandle);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error freeing previous PulseAudio handle");
                    }
                    _paHandle = IntPtr.Zero;
                }

                // Determine actual sample rate to use (output format overrides incoming)
                var actualSampleRate = _outputFormat?.SampleRate ?? format.SampleRate;

                _logger.LogInformation(
                    "Initializing PulseAudio player: {SampleRate}Hz, {Channels}ch, {BitDepth}-bit, sink: {Sink}",
                    actualSampleRate, format.Channels,
                    _outputFormat?.BitDepth ?? 32, _sinkName ?? "default");

                // Create sample spec for PulseAudio
                var sampleSpec = new SampleSpec
                {
                    Format = _paFormat,
                    Rate = (uint)actualSampleRate,
                    Channels = (byte)format.Channels
                };

                // Configure buffer attributes for low latency
                var targetLatencyBytes = BytesForMs(ref sampleSpec, BufferMs);
                var bufferAttr = new BufferAttr
                {
                    MaxLength = uint.MaxValue,  // Let server decide
                    TLength = targetLatencyBytes,
                    PreBuf = targetLatencyBytes / 2,
                    MinReq = uint.MaxValue,     // Let server decide
                    FragSize = uint.MaxValue    // Not used for playback
                };

                // Connect to PulseAudio
                _paHandle = SimpleNewWithAttr(
                    server: null,  // Use default server (PULSE_SERVER env var)
                    name: "MultiRoomAudio",
                    dir: StreamDirection.Playback,
                    dev: _sinkName,  // null = default sink
                    streamName: "Sendspin Audio",
                    ss: ref sampleSpec,
                    map: IntPtr.Zero,
                    attr: ref bufferAttr,
                    error: out var error);

                if (_paHandle == IntPtr.Zero)
                {
                    var errorMsg = GetErrorMessage(error);
                    throw new InvalidOperationException($"Failed to connect to PulseAudio: {errorMsg}");
                }

                _currentFormat = format;

                // Get actual latency
                var latencyUs = SimpleGetLatency(_paHandle, out var latencyError);
                if (latencyError != 0)
                {
                    _logger.LogWarning(
                        "Could not query PulseAudio latency: {Error}. Using fallback {BufferMs}ms",
                        GetErrorMessage(latencyError), BufferMs);
                    OutputLatencyMs = BufferMs;
                }
                else
                {
                    OutputLatencyMs = (int)(latencyUs / 1000);
                    if (OutputLatencyMs <= 0)
                    {
                        _logger.LogDebug(
                            "PulseAudio reported invalid latency {Latency}μs. Using fallback {BufferMs}ms",
                            latencyUs, BufferMs);
                        OutputLatencyMs = BufferMs;
                    }
                }

                // Pre-allocate buffers
                var samplesPerWrite = FramesPerWrite * format.Channels;
                _sampleBuffer = new float[samplesPerWrite];
                _byteBuffer = new byte[samplesPerWrite * _bytesPerSample];

                SetState(AudioPlayerState.Stopped);

                _logger.LogInformation(
                    "PulseAudio player initialized. Sink: {Sink}, Latency: {Latency}ms",
                    _sinkName ?? "default", OutputLatencyMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize PulseAudio player");
                SetState(AudioPlayerState.Error);
                OnError("Initialization failed", ex);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public void SetSampleSource(IAudioSampleSource source)
    {
        lock (_lock)
        {
            _sampleSource = source;
            _logger.LogDebug("Sample source set");
        }
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_paHandle == IntPtr.Zero)
            {
                _logger.LogWarning("Cannot play - not initialized");
                return;
            }

            if (_isPlaying && !_isPaused)
            {
                _logger.LogDebug("Already playing");
                return;
            }

            if (_isPaused)
            {
                // Resume from pause
                _isPaused = false;
                SetState(AudioPlayerState.Playing);
                _logger.LogInformation("Playback resumed");
                return;
            }

            // Start playback thread
            _playbackCts = new CancellationTokenSource();
            _isPlaying = true;
            _isPaused = false;

            _playbackThread = new Thread(PlaybackLoop)
            {
                Name = "PulseAudio-Playback",
                IsBackground = true
            };

            try
            {
                _playbackThread.Priority = ThreadPriority.AboveNormal;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Could not set thread priority to AboveNormal. Audio may have higher latency. " +
                    "This is normal in containers without elevated privileges.");
            }

            _playbackThread.Start();

            SetState(AudioPlayerState.Playing);
            _logger.LogInformation("Playback started");
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_isPlaying)
                return;

            _isPaused = true;
            SetState(AudioPlayerState.Paused);
            _logger.LogInformation("Playback paused");
        }
    }

    public void Stop()
    {
        Thread? threadToJoin = null;

        lock (_lock)
        {
            if (!_isPlaying)
                return;

            _isPlaying = false;
            _isPaused = false;
            _playbackCts?.Cancel();
            threadToJoin = _playbackThread;
        }

        // Wait for thread outside lock - increased timeout for safety
        if (threadToJoin != null)
        {
            if (!threadToJoin.Join(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning(
                    "Playback thread did not exit within 5 seconds, may be hung in native call");
            }
        }

        lock (_lock)
        {
            _playbackThread = null;
            _playbackCts?.Dispose();
            _playbackCts = null;

            // Flush any remaining audio
            if (_paHandle != IntPtr.Zero)
            {
                SimpleFlush(_paHandle, out _);
            }

            SetState(AudioPlayerState.Stopped);
            _logger.LogInformation("Playback stopped");
        }
    }

    public async Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Switching PulseAudio sink to: {Sink}", deviceId ?? "default");

        var wasPlaying = State == AudioPlayerState.Playing;
        var savedSource = _sampleSource;
        var savedFormat = _currentFormat;
        var savedSinkName = _sinkName;
        IntPtr oldHandle = IntPtr.Zero;

        // Stop current playback
        Stop();

        // Save the old handle but don't free it yet (in case we need to restore)
        lock (_lock)
        {
            oldHandle = _paHandle;
            _paHandle = IntPtr.Zero;
        }

        // Update sink name
        _sinkName = deviceId;

        // Reinitialize with new sink
        if (savedFormat != null)
        {
            try
            {
                await InitializeAsync(savedFormat, cancellationToken);

                // Only free old handle after successful initialization
                if (oldHandle != IntPtr.Zero)
                {
                    SimpleFree(oldHandle);
                    oldHandle = IntPtr.Zero;
                }

                if (savedSource != null)
                {
                    SetSampleSource(savedSource);
                }

                if (wasPlaying)
                {
                    Play();
                }

                _logger.LogInformation("Device switch complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device switch failed, restoring previous state");

                // Restore the old handle on failure
                lock (_lock)
                {
                    if (_paHandle == IntPtr.Zero && oldHandle != IntPtr.Zero)
                    {
                        _paHandle = oldHandle;
                        oldHandle = IntPtr.Zero; // Prevent cleanup
                        _sinkName = savedSinkName;
                        SetState(AudioPlayerState.Stopped);
                    }
                }

                OnError("Device switch failed", ex);
                throw;
            }
            finally
            {
                // Clean up old handle if not restored
                if (oldHandle != IntPtr.Zero)
                {
                    SimpleFree(oldHandle);
                }
            }
        }
    }

    private void PlaybackLoop()
    {
        _logger.LogDebug("Playback thread started");
        int consecutiveFailures = 0;

        try
        {
            var ct = _playbackCts?.Token ?? CancellationToken.None;

            while (_isPlaying && !ct.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    // When paused, sleep briefly and check again
                    Thread.Sleep(10);
                    continue;
                }

                var source = _sampleSource;
                var buffer = _sampleBuffer;
                var byteBuffer = _byteBuffer;

                if (source == null || buffer == null || byteBuffer == null)
                {
                    // No source or not initialized - output silence
                    Thread.Sleep(10);
                    continue;
                }

                // Capture handle under lock to prevent race with disposal/reconnection
                IntPtr handle;
                lock (_lock)
                {
                    handle = _paHandle;
                }

                // Check if handle is valid, attempt reconnect if needed
                if (handle == IntPtr.Zero)
                {
                    _logger.LogWarning("PulseAudio handle is null - attempting reconnection...");
                    if (!TryReconnect())
                    {
                        _logger.LogError("PulseAudio reconnection failed - stopping playback");
                        OnError("PulseAudio connection lost and could not reconnect", null);
                        break;
                    }
                    consecutiveFailures = 0;
                    continue;
                }

                // Read samples from source
                var samplesRead = source.Read(buffer, 0, buffer.Length);

                if (samplesRead == 0)
                {
                    // No data available - small sleep to avoid busy loop
                    Thread.Sleep(1);
                    continue;
                }

                // Apply volume and mute
                var vol = IsMuted ? 0f : Volume;
                for (int i = 0; i < samplesRead; i++)
                {
                    buffer[i] *= vol;
                }

                // Convert float samples to output format
                if (_needsBitDepthConversion)
                {
                    var bitDepth = _outputFormat?.BitDepth ?? 32;
                    BitDepthConverter.Convert(
                        buffer.AsSpan(0, samplesRead),
                        byteBuffer.AsSpan(0, samplesRead * _bytesPerSample),
                        bitDepth);
                }
                else
                {
                    // Direct copy for float output
                    Buffer.BlockCopy(buffer, 0, byteBuffer, 0, samplesRead * sizeof(float));
                }

                // Write to PulseAudio using the captured handle
                // Note: The handle was captured at loop start; if disposal occurred mid-loop,
                // the native call may fail but won't crash due to null pointer
                bool writeSuccess = false;
                unsafe
                {
                    fixed (byte* ptr = byteBuffer)
                    {
                        var result = SimpleWrite(
                            handle,
                            (IntPtr)ptr,
                            (UIntPtr)(samplesRead * _bytesPerSample),
                            out var error);

                        if (result < 0)
                        {
                            var errorMsg = GetErrorMessage(error);
                            consecutiveFailures++;

                            if (consecutiveFailures >= MaxConsecutiveFailures)
                            {
                                _logger.LogWarning(
                                    "PulseAudio write failed {Count} consecutive times: {Error}. Attempting reconnection...",
                                    consecutiveFailures, errorMsg);

                                if (!TryReconnect())
                                {
                                    _logger.LogError("PulseAudio reconnection failed - stopping playback");
                                    OnError($"PulseAudio connection lost: {errorMsg}", null);
                                    break;
                                }
                                consecutiveFailures = 0;
                            }
                            else
                            {
                                _logger.LogDebug(
                                    "PulseAudio write error ({Count}/{Max}): {Error}",
                                    consecutiveFailures, MaxConsecutiveFailures, errorMsg);
                                Thread.Sleep(10);
                            }
                        }
                        else
                        {
                            writeSuccess = true;
                        }
                    }
                }

                // Reset failure counter on successful write
                if (writeSuccess)
                {
                    consecutiveFailures = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in playback thread");
            OnError("Playback error", ex);
        }

        _logger.LogDebug("Playback thread exited");
    }

    /// <summary>
    /// Attempts to reconnect to PulseAudio with exponential backoff.
    /// Called when the playback loop detects consecutive write failures.
    /// </summary>
    /// <returns>True if reconnection succeeded, false otherwise.</returns>
    private bool TryReconnect()
    {
        if (_currentFormat == null)
        {
            _logger.LogError("Cannot reconnect - no format configured");
            return false;
        }

        _logger.LogWarning("PulseAudio connection lost - attempting to reconnect...");

        // Close the dead handle if it exists
        lock (_lock)
        {
            if (_paHandle != IntPtr.Zero)
            {
                try
                {
                    SimpleFree(_paHandle);
                }
                catch
                {
                    // Ignore errors when freeing dead handle
                }
                _paHandle = IntPtr.Zero;
            }
        }

        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            // Calculate delay with exponential backoff
            var delay = Math.Min(ReconnectBaseDelayMs * (1 << (attempt - 1)), ReconnectMaxDelayMs);

            _logger.LogInformation(
                "PulseAudio reconnect attempt {Attempt}/{MaxAttempts} in {Delay}ms...",
                attempt, MaxReconnectAttempts, delay);

            Thread.Sleep(delay);

            // Check if we should stop trying
            if (!_isPlaying || _disposed)
            {
                _logger.LogDebug("Reconnection cancelled - player stopped or disposed");
                return false;
            }

            try
            {
                // Use configured output sample rate if specified
                var actualSampleRate = _outputFormat?.SampleRate ?? _currentFormat.SampleRate;

                // Create new connection
                var sampleSpec = new SampleSpec
                {
                    Format = _paFormat,
                    Rate = (uint)actualSampleRate,
                    Channels = (byte)_currentFormat.Channels
                };

                var targetLatencyBytes = BytesForMs(ref sampleSpec, BufferMs);
                var bufferAttr = new BufferAttr
                {
                    MaxLength = uint.MaxValue,
                    TLength = targetLatencyBytes,
                    PreBuf = targetLatencyBytes / 2,
                    MinReq = uint.MaxValue,
                    FragSize = uint.MaxValue
                };

                var newHandle = SimpleNewWithAttr(
                    server: null,
                    name: "MultiRoomAudio",
                    dir: StreamDirection.Playback,
                    dev: _sinkName,
                    streamName: "Sendspin Audio",
                    ss: ref sampleSpec,
                    map: IntPtr.Zero,
                    attr: ref bufferAttr,
                    error: out var error);

                if (newHandle != IntPtr.Zero)
                {
                    lock (_lock)
                    {
                        _paHandle = newHandle;
                    }

                    _logger.LogInformation(
                        "PulseAudio reconnected successfully on attempt {Attempt}",
                        attempt);
                    return true;
                }

                var errorMsg = GetErrorMessage(error);
                _logger.LogWarning(
                    "PulseAudio reconnect attempt {Attempt} failed: {Error}",
                    attempt, errorMsg);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PulseAudio reconnect attempt {Attempt} threw exception",
                    attempt);
            }
        }

        _logger.LogError(
            "PulseAudio reconnection failed after {MaxAttempts} attempts",
            MaxReconnectAttempts);
        return false;
    }

    private void SetState(AudioPlayerState newState)
    {
        if (State != newState)
        {
            var oldState = State;
            State = newState;
            _logger.LogDebug("State changed: {OldState} -> {NewState}", oldState, newState);
            StateChanged?.Invoke(this, newState);
        }
    }

    private void OnError(string message, Exception? ex = null)
    {
        ErrorOccurred?.Invoke(this, new AudioPlayerError(message, ex));
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            _disposed = true;
        }

        // Stop playback (waits for thread)
        Stop();

        IntPtr handleToFree;
        lock (_lock)
        {
            // Capture the handle and set to zero BEFORE freeing
            // This ensures the playback thread sees the zero and skips writes
            handleToFree = _paHandle;
            _paHandle = IntPtr.Zero;

            _sampleBuffer = null;
            _byteBuffer = null;
        }

        // Free the handle outside the lock (safe - playback thread has stopped)
        if (handleToFree != IntPtr.Zero)
        {
            SimpleFree(handleToFree);
        }

        _logger.LogInformation("PulseAudio player disposed");
        return ValueTask.CompletedTask;
    }
}
