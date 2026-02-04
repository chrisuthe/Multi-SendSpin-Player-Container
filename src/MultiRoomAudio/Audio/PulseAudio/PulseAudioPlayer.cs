using System.Runtime.InteropServices;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using static MultiRoomAudio.Audio.PulseAudio.PulseAudioNative;

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// IAudioPlayer implementation using PulseAudio's async pa_stream API.
/// Uses callbacks for accurate real-time latency measurement.
/// </summary>
/// <remarks>
/// This player uses a threaded mainloop with write callbacks. PulseAudio calls
/// our write callback when it needs audio data, and we query the actual latency
/// at that moment for accurate sync correction.
/// </remarks>
public class PulseAudioPlayer : IAudioPlayer
{
    private readonly ILogger<PulseAudioPlayer> _logger;
    private readonly object _lock = new();

    // PulseAudio async API handles
    private IntPtr _mainloop = IntPtr.Zero;
    private IntPtr _context = IntPtr.Zero;
    private IntPtr _stream = IntPtr.Zero;

    // CRITICAL: Store callbacks as fields to prevent GC collection
    // If these get collected, PulseAudio will call garbage memory and crash
    private ContextNotifyCallback? _contextStateCallback;
    private StreamNotifyCallback? _streamStateCallback;
    private StreamRequestCallback? _writeCallback;
    private StreamNotifyCallback? _underflowCallback;

    // THREAD SAFETY: These fields are accessed from both the main thread (via public API)
    // and PulseAudio's internal mainloop thread (via write callback). Using volatile ensures
    // visibility of writes across threads. The write callback runs on PA's thread which already
    // holds the mainloop lock, so we cannot take _lock there - volatile is our synchronization.
    private volatile IAudioSampleSource? _sampleSource;
    private AudioFormat? _currentFormat;
    private string? _sinkName;
    private volatile bool _disposed;

    private volatile bool _isPlaying;
    private volatile bool _isPaused;
    private volatile bool _contextReady;
    private volatile bool _streamReady;

    // Pre-allocated buffers for the write callback.
    // These are set once during initialization and read from the callback thread.
    // Volatile ensures the callback sees the initialized values.
    private volatile float[]? _sampleBuffer;
    private volatile byte[]? _byteBuffer;

    // Pre-allocated silence buffer to avoid GC allocations in the write callback.
    // Resized as needed but typically stays at the initial size.
    private byte[] _silenceBuffer = new byte[8192];

    /// <summary>
    /// Target buffer size in milliseconds. PulseAudio will request ~this much audio.
    /// </summary>
    private const int BufferMs = 50;

    /// <summary>
    /// Initial latency estimate before real measurements are available.
    /// </summary>
    private const int InitialLatencyEstimateMs = 70;

    /// <summary>
    /// Frames to request per write. At 48kHz, 6144 frames = ~128ms.
    /// This accommodates large PA requests in VM environments where
    /// callbacks may be delayed and PA compensates by requesting more data.
    /// </summary>
    private const int FramesPerWrite = 6144;

    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    private const int ConnectionTimeoutMs = 10000;

    /// <summary>
    /// Number of underflows before logging a warning.
    /// </summary>
    private const int UnderflowWarningThreshold = 5;

    /// <summary>
    /// How often to log diagnostic info (in callbacks).
    /// At ~10ms per callback, 100 = log every ~1 second.
    /// </summary>
    private const int DiagnosticLogInterval = 100;

    private int _underflowCount;
    private ulong _lastMeasuredLatencyUs;

    // Latency lock-in: collect samples during startup, then freeze to median
    // This prevents PulseAudio measurement jitter from causing constant sync corrections
    private volatile bool _latencyLocked;
    private List<int>? _latencySamples;
    private const int LatencyLockSampleCount = 100;  // ~1 second at 10ms callbacks
    private const int LatencyLockWarmupSamples = 20; // Skip first 20 (~200ms) for warmup

    // Diagnostic counters for monitoring callback behavior
    private long _callbackCount;
    private long _silenceWriteCount;
    private long _zeroReadCount;
    private DateTime _playbackStartTime;
    private bool _hasLoggedFirstAudio;

    // Audio clock: Unix epoch microseconds when playback started (captured at uncork time).
    // Used to convert pa_stream_get_time() (relative) to absolute Unix time.
    private long _playbackStartUnixMicroseconds;

    // Stream time captured immediately after uncork.
    // Subtracted from subsequent readings to get time since actual playback start.
    // This handles any non-zero stream time that exists right after uncork.
    private long _streamTimeAtUncorkMicroseconds;

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

    /// <summary>
    /// Gets the output latency in milliseconds, measured from write callbacks.
    /// After the lock-in period (~1.2 seconds), this value freezes at the median
    /// to prevent PulseAudio measurement jitter from causing constant sync corrections.
    /// </summary>
    public int OutputLatencyMs { get; private set; }

    /// <summary>
    /// Gets whether the output latency has stabilized and locked.
    /// When true, OutputLatencyMs will no longer update until playback restarts.
    /// </summary>
    public bool IsLatencyLocked => _latencyLocked;

    /// <summary>
    /// Gets the current playback time from the PulseAudio stream in microseconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This returns time in the sound card's clock domain via <c>pa_stream_get_time()</c>,
    /// making it immune to VM wall clock issues. The time represents how much audio has
    /// actually been played through the DAC.
    /// </para>
    /// <para>
    /// Returns <c>null</c> when:
    /// - Not currently playing
    /// - Stream is not ready
    /// - PulseAudio timing info is not available
    /// </para>
    /// </remarks>
    /// <returns>
    /// Audio hardware clock time in microseconds, or <c>null</c> if not available.
    /// </returns>
    public long? GetAudioClockMicroseconds()
    {
        // THREAD SAFETY: Capture handles under lock to prevent race condition.
        // The SDK calls this from its timing thread while playback may be stopping
        // on another thread. Without the lock, _mainloop could become IntPtr.Zero
        // after the null check but before ThreadedMainloopLock(), causing a segfault.
        IntPtr mainloop;
        IntPtr stream;
        long startTimeUs;
        long streamTimeAtUncork;

        lock (_lock)
        {
            // Early exit if not in a valid state for timing queries
            if (!_isPlaying || _disposed || _stream == IntPtr.Zero || _mainloop == IntPtr.Zero)
            {
                return null;
            }

            // Capture handles and timing values while holding _lock
            mainloop = _mainloop;
            stream = _stream;
            startTimeUs = _playbackStartUnixMicroseconds;
            streamTimeAtUncork = _streamTimeAtUncorkMicroseconds;
        }

        // Check if we're already on the PulseAudio mainloop thread (i.e., called from a callback).
        // If so, we must NOT call ThreadedMainloopLock() - the callback already holds the lock
        // implicitly, and trying to lock again causes PulseAudio to abort with an assertion.
        var inCallbackThread = ThreadedMainloopInThread(mainloop) != 0;

        if (!inCallbackThread)
        {
            ThreadedMainloopLock(mainloop);
        }

        try
        {
            // StreamGetTime returns 0 on success, negative on error.
            // The returned value is μs since stream started (relative time).
            if (StreamGetTime(stream, out var streamTimeUs) == 0)
            {
                // Return Unix epoch microseconds: baseline + (current_stream_time - stream_time_at_uncork)
                // This gives us elapsed time since playback actually started, converted to absolute time.
                return startTimeUs + (long)streamTimeUs - streamTimeAtUncork;
            }

            // Timing info not available (PA_ERR_NODATA)
            return null;
        }
        finally
        {
            if (!inCallbackThread)
            {
                ThreadedMainloopUnlock(mainloop);
            }
        }
    }

    public event EventHandler<AudioPlayerState>? StateChanged;
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the PulseAudioPlayer.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="sinkName">
    /// Optional PulseAudio sink name. If null, uses the default sink.
    /// </param>
    public PulseAudioPlayer(ILogger<PulseAudioPlayer> logger, string? sinkName = null)
    {
        _logger = logger;
        _sinkName = sinkName;
    }

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        // Stop any existing playback first
        if (_isPlaying)
        {
            _logger.LogDebug("Stopping active playback before re-initialization");
            Stop();
        }

        lock (_lock)
        {
            // Reset disposed flag to allow re-initialization of the same instance.
            // The SDK reuses player instances via playerFactory, so we must support
            // being re-initialized after a previous disposal.
            _disposed = false;

            try
            {
                // Clean up any existing resources
                CleanupResources();

                _logger.LogInformation(
                    "Initializing PulseAudio player: {SampleRate}Hz, {Channels}ch, FLOAT32, sink: {Sink}",
                    format.SampleRate, format.Channels, _sinkName ?? "default");

                // Create the threaded mainloop
                _mainloop = ThreadedMainloopNew();
                if (_mainloop == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create PulseAudio mainloop");
                }

                // Get the mainloop API
                var api = ThreadedMainloopGetApi(_mainloop);
                if (api == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to get PulseAudio mainloop API");
                }

                // Create the context
                _context = ContextNew(api, "MultiRoomAudio");
                if (_context == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create PulseAudio context");
                }

                // Set up context state callback (store delegate to prevent GC)
                _contextStateCallback = OnContextStateChanged;
                ContextSetStateCallback(_context, _contextStateCallback, IntPtr.Zero);

                // Start the mainloop
                if (ThreadedMainloopStart(_mainloop) < 0)
                {
                    throw new InvalidOperationException("Failed to start PulseAudio mainloop");
                }

                // Connect to PulseAudio server
                ThreadedMainloopLock(_mainloop);
                try
                {
                    if (ContextConnect(_context, null, 0, IntPtr.Zero) < 0)
                    {
                        throw new InvalidOperationException("Failed to connect to PulseAudio server");
                    }

                    // Wait for context to be ready
                    var timeout = DateTime.UtcNow.AddMilliseconds(ConnectionTimeoutMs);
                    while (!_contextReady)
                    {
                        var state = ContextGetState(_context);
                        if (state == ContextState.Failed || state == ContextState.Terminated)
                        {
                            var errorMsg = GetContextError(_context);
                            throw new InvalidOperationException($"PulseAudio context failed: {errorMsg}");
                        }

                        if (DateTime.UtcNow > timeout)
                        {
                            throw new TimeoutException("Timeout waiting for PulseAudio context");
                        }

                        ThreadedMainloopWait(_mainloop);
                    }
                }
                finally
                {
                    ThreadedMainloopUnlock(_mainloop);
                }

                _logger.LogDebug("PulseAudio context connected");

                // Create the stream
                var sampleSpec = new SampleSpec
                {
                    Format = SampleFormat.FLOAT32LE,
                    Rate = (uint)format.SampleRate,
                    Channels = (byte)format.Channels
                };

                ThreadedMainloopLock(_mainloop);
                try
                {
                    _stream = StreamNew(_context, "Sendspin Audio", ref sampleSpec, IntPtr.Zero);
                    if (_stream == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Failed to create PulseAudio stream");
                    }

                    // Set up stream callbacks (store delegates to prevent GC)
                    _streamStateCallback = OnStreamStateChanged;
                    _writeCallback = OnWriteCallback;
                    _underflowCallback = OnUnderflow;

                    StreamSetStateCallback(_stream, _streamStateCallback, IntPtr.Zero);
                    StreamSetWriteCallback(_stream, _writeCallback, IntPtr.Zero);
                    StreamSetUnderflowCallback(_stream, _underflowCallback, IntPtr.Zero);

                    // Configure buffer attributes for low-latency playback.
                    // Per PulseAudio docs: set fields to uint.MaxValue (-1) to let PA choose defaults,
                    // except for the fields we want to control.
                    var targetLatencyBytes = BytesForMs(ref sampleSpec, BufferMs);
                    var minReqBytes = BytesForMs(ref sampleSpec, 10); // Request callbacks every ~10ms
                    var bufferAttr = new BufferAttr
                    {
                        // MaxLength: Maximum buffer size. Let PA choose.
                        MaxLength = uint.MaxValue,
                        // TLength: Target buffer length (our latency target of 50ms).
                        TLength = targetLatencyBytes,
                        // PreBuf: Prebuffering amount before playback starts.
                        // Set to 0 to start immediately when uncorked - we handle underflows gracefully.
                        // Previously was targetLatencyBytes/2 which delayed startup by ~25ms.
                        PreBuf = 0,
                        // MinReq: Minimum request size for write callbacks.
                        // Smaller = more frequent callbacks = better responsiveness to timing changes.
                        // ~10ms gives good balance between responsiveness and CPU overhead.
                        MinReq = minReqBytes,
                        // FragSize: Fragment size (recording only, not relevant for playback).
                        FragSize = uint.MaxValue
                    };

                    // Connect stream for playback with timing flags.
                    // StartCorked: Stream starts paused - audio won't flow until explicitly uncorked
                    // in Play(). This prevents audio playing before sample source is ready and ensures
                    // timing info is available before playback begins.
                    // InterpolateTiming + AutoTimingUpdate: Enable latency interpolation with automatic
                    // timing updates every 100ms, allowing accurate pa_stream_get_latency() calls.
                    // AdjustLatency: Tell PA to reconfigure hardware buffers to meet our target latency.
                    var flags = StreamFlags.StartCorked |
                                StreamFlags.InterpolateTiming |
                                StreamFlags.AutoTimingUpdate |
                                StreamFlags.AdjustLatency;

                    if (StreamConnectPlayback(_stream, _sinkName, ref bufferAttr, flags, IntPtr.Zero, IntPtr.Zero) < 0)
                    {
                        throw new InvalidOperationException("Failed to connect PulseAudio stream");
                    }

                    // Wait for stream to be ready
                    var timeout = DateTime.UtcNow.AddMilliseconds(ConnectionTimeoutMs);
                    while (!_streamReady)
                    {
                        var state = StreamGetState(_stream);
                        if (state == StreamState.Failed || state == StreamState.Terminated)
                        {
                            var errorMsg = GetContextError(_context);
                            throw new InvalidOperationException(
                                $"PulseAudio stream failed: {errorMsg}. Sink: {_sinkName ?? "default"}");
                        }

                        if (DateTime.UtcNow > timeout)
                        {
                            throw new TimeoutException("Timeout waiting for PulseAudio stream");
                        }

                        ThreadedMainloopWait(_mainloop);
                    }

                    // Request initial timing info update.
                    // pa_stream_get_latency() returns PA_ERR_NODATA until timing info is available.
                    // With AUTO_TIMING_UPDATE, PA updates timing every ~100ms, but we request
                    // an immediate update so latency is available when Play() is called.
                    StreamUpdateTimingInfo(_stream, IntPtr.Zero, IntPtr.Zero);
                }
                finally
                {
                    ThreadedMainloopUnlock(_mainloop);
                }

                _currentFormat = format;

                // Set initial latency estimate; will be updated by write callback
                OutputLatencyMs = InitialLatencyEstimateMs;

                // Pre-allocate buffers
                var samplesPerWrite = FramesPerWrite * format.Channels;
                _sampleBuffer = new float[samplesPerWrite];
                _byteBuffer = new byte[samplesPerWrite * sizeof(float)];

                SetState(AudioPlayerState.Stopped);

                _logger.LogInformation(
                    "PulseAudio player initialized (async API). Sink: {Sink}, Initial latency estimate: {Latency}ms",
                    _sinkName ?? "default", OutputLatencyMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize PulseAudio player");
                CleanupResources();
                SetState(AudioPlayerState.Error);
                OnError("Initialization failed", ex);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public void SetSampleSource(IAudioSampleSource source)
    {
        // No lock needed - _sampleSource is volatile for cross-thread visibility.
        // The write callback will see this value on its next iteration.
        _sampleSource = source;
        _logger.LogDebug("Sample source set");
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_stream == IntPtr.Zero)
            {
                _logger.LogWarning("Cannot play - not initialized");
                return;
            }

            if (_isPlaying && !_isPaused)
            {
                _logger.LogDebug("Already playing");
                return;
            }

            _isPlaying = true;
            _isPaused = false;

            // Reset diagnostic counters for fresh monitoring
            _callbackCount = 0;
            _silenceWriteCount = 0;
            _zeroReadCount = 0;
            _underflowCount = 0;
            _hasLoggedFirstAudio = false;
            _playbackStartTime = DateTime.UtcNow;

            // Uncork the stream and capture timing baseline IMMEDIATELY after.
            // CRITICAL: Both Unix time and stream time must be captured right after uncork
            // to establish an accurate baseline for audio clock synchronization.
            ThreadedMainloopLock(_mainloop);
            try
            {
                // Uncork the stream to start/resume playback.
                // Stream is connected with StartCorked flag, so we must uncork to begin.
                StreamCork(_stream, 0, IntPtr.Zero, IntPtr.Zero);

                // Capture Unix epoch time immediately after uncork.
                // This is our baseline for converting stream time to absolute time.
                _playbackStartUnixMicroseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

                // Capture stream time immediately after uncork.
                // pa_stream_get_time() may or may not reset on uncork depending on PulseAudio version.
                // By capturing this value now and subtracting it from future readings,
                // we measure time elapsed since playback actually started.
                if (StreamGetTime(_stream, out var streamTimeAtUncork) == 0)
                {
                    _streamTimeAtUncorkMicroseconds = (long)streamTimeAtUncork;
                    _logger.LogDebug("Audio clock baseline captured: Unix={UnixMs}ms, StreamTime={StreamTimeUs}μs ({StreamTimeMs:F1}ms)",
                        _playbackStartUnixMicroseconds / 1000,
                        _streamTimeAtUncorkMicroseconds,
                        _streamTimeAtUncorkMicroseconds / 1000.0);
                }
                else
                {
                    _streamTimeAtUncorkMicroseconds = 0;
                    _logger.LogDebug("Audio clock baseline captured: Unix={UnixMs}ms, StreamTime=unavailable",
                        _playbackStartUnixMicroseconds / 1000);
                }
            }
            finally
            {
                ThreadedMainloopUnlock(_mainloop);
            }

            SetState(AudioPlayerState.Playing);
            _logger.LogInformation("Playback started (stream uncorked). Monitoring callbacks...");
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_isPlaying || _stream == IntPtr.Zero)
                return;

            _isPaused = true;

            // Cork (pause) the stream
            ThreadedMainloopLock(_mainloop);
            try
            {
                StreamCork(_stream, 1, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                ThreadedMainloopUnlock(_mainloop);
            }

            SetState(AudioPlayerState.Paused);
            _logger.LogInformation("Playback paused");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isPlaying = false;
            _isPaused = false;

            if (_stream != IntPtr.Zero && _mainloop != IntPtr.Zero)
            {
                ThreadedMainloopLock(_mainloop);
                try
                {
                    // Flush any remaining audio
                    StreamFlush(_stream, IntPtr.Zero, IntPtr.Zero);
                }
                finally
                {
                    ThreadedMainloopUnlock(_mainloop);
                }
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

        Stop();

        _sinkName = deviceId;

        // Reset latency lock to re-learn for the new device
        // Different audio devices have different latency characteristics
        _latencyLocked = false;
        _latencySamples = null;

        if (savedFormat != null)
        {
            try
            {
                // Clean up old resources
                lock (_lock)
                {
                    CleanupResources();
                }

                await InitializeAsync(savedFormat, cancellationToken);

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
                _logger.LogError(ex, "Device switch failed");
                OnError("Device switch failed", ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Called by PulseAudio when the context state changes.
    /// </summary>
    private void OnContextStateChanged(IntPtr context, IntPtr userdata)
    {
        var state = ContextGetState(context);
        _logger.LogDebug("PulseAudio context state: {State}", state);

        if (state == ContextState.Ready)
        {
            _contextReady = true;
            ThreadedMainloopSignal(_mainloop, 0);
        }
        else if (state == ContextState.Failed || state == ContextState.Terminated)
        {
            _contextReady = false;
            ThreadedMainloopSignal(_mainloop, 0);

            if (state == ContextState.Terminated && (_disposed || !_isPlaying))
                _logger.LogDebug("PulseAudio context disconnected (expected): {State}", state);
            else
                _logger.LogWarning("PulseAudio context disconnected: {State}", state);
        }
    }

    /// <summary>
    /// Called by PulseAudio when the stream state changes.
    /// </summary>
    private void OnStreamStateChanged(IntPtr stream, IntPtr userdata)
    {
        var state = StreamGetState(stream);
        _logger.LogDebug("PulseAudio stream state: {State}", state);

        if (state == StreamState.Ready)
        {
            _streamReady = true;
            ThreadedMainloopSignal(_mainloop, 0);
        }
        else if (state == StreamState.Failed || state == StreamState.Terminated)
        {
            _streamReady = false;
            ThreadedMainloopSignal(_mainloop, 0);

            // Get actual error from PulseAudio context
            var errorMsg = _context != IntPtr.Zero ? GetContextError(_context) : "Unknown";

            if (state == StreamState.Terminated && (_disposed || !_isPlaying))
                _logger.LogDebug("PulseAudio stream disconnected (expected): {State}. Sink: {Sink}",
                    state, _sinkName ?? "default");
            else
                _logger.LogWarning("PulseAudio stream disconnected: {State}. Error: {Error}. Sink: {Sink}",
                    state, errorMsg, _sinkName ?? "default");
        }
    }

    /// <summary>
    /// Called by PulseAudio when it needs more audio data.
    /// </summary>
    /// <remarks>
    /// THREADING: This callback runs on PulseAudio's internal mainloop thread, which already
    /// holds the mainloop lock. Do NOT call ThreadedMainloopLock() here - it would deadlock.
    /// All shared state accessed here must be volatile or otherwise thread-safe.
    ///
    /// PERFORMANCE: This callback must complete quickly. Blocking or slow operations will
    /// cause audio underflows. The sample source Read() should be fast and non-blocking.
    ///
    /// LATENCY: We query pa_stream_get_latency() here to get accurate real-time latency
    /// measurements. This is the key benefit of using pa_stream over pa_simple.
    /// </remarks>
    private void OnWriteCallback(IntPtr stream, UIntPtr nbytes, IntPtr userdata)
    {
        // Track callback for diagnostics
        _callbackCount++;

        // Early exit if not in playing state. Stream starts corked, so this handles
        // callbacks that might occur during initialization before Play() is called.
        if (!_isPlaying || _isPaused || _disposed)
        {
            WriteSilence(stream, nbytes);
            _silenceWriteCount++;
            return;
        }

        // Query real-time latency from PulseAudio.
        // Returns 0 on success, negative on error (e.g., PA_ERR_NODATA if timing not ready).
        // The 'negative' out param indicates if latency is negative (stream ahead of playback).
        // With INTERPOLATE_TIMING + AUTO_TIMING_UPDATE flags, this gives accurate values
        // interpolated between the ~100ms server updates.
        if (StreamGetLatency(stream, out var latencyUs, out var negative) == 0 && negative == 0)
        {
            _lastMeasuredLatencyUs = latencyUs;
            var newLatencyMs = (int)(latencyUs / 1000);

            if (!_latencyLocked)
            {
                // During startup: collect samples for lock-in
                // USB audio devices report jittery latency values (±25-50ms) due to
                // PulseAudio's timer-based scheduling and USB isochronous transfer timing.
                // We collect samples and lock to the median to avoid constant sync corrections.
                _latencySamples ??= new List<int>(LatencyLockSampleCount + LatencyLockWarmupSamples);
                _latencySamples.Add(newLatencyMs);

                if (_latencySamples.Count >= LatencyLockSampleCount + LatencyLockWarmupSamples)
                {
                    // Discard warmup samples, compute median of the rest
                    var stableSamples = _latencySamples.Skip(LatencyLockWarmupSamples).OrderBy(x => x).ToList();
                    var median = stableSamples[stableSamples.Count / 2];

                    OutputLatencyMs = median;
                    _latencyLocked = true;
                    _latencySamples = null; // Free memory

                    _logger.LogInformation(
                        "Latency locked at {Latency}ms (median of {Count} samples, range: {Min}-{Max}ms)",
                        OutputLatencyMs, stableSamples.Count, stableSamples.First(), stableSamples.Last());
                }
                else
                {
                    // During collection: use current measurement (with hysteresis)
                    if (Math.Abs(newLatencyMs - OutputLatencyMs) > 5)
                    {
                        OutputLatencyMs = newLatencyMs;
                    }
                }
            }
            // After lock: OutputLatencyMs stays frozen, no updates
        }

        // Read volatile fields into locals for consistent access within this callback.
        // The volatile keyword ensures we see the latest values written by other threads.
        var source = _sampleSource;
        var sampleBuffer = _sampleBuffer;
        var byteBuffer = _byteBuffer;

        if (source == null || sampleBuffer == null || byteBuffer == null)
        {
            // Sample source not set yet - write silence to keep stream happy
            WriteSilence(stream, nbytes);
            _silenceWriteCount++;

            // Log periodically during startup when source isn't ready
            if (_callbackCount % DiagnosticLogInterval == 0)
            {
                _logger.LogDebug(
                    "Waiting for sample source: callbacks={Callbacks}, silence={Silence}",
                    _callbackCount, _silenceWriteCount);
            }
            return;
        }

        var bytesRequested = (int)(ulong)nbytes;
        var bytesPerSample = sizeof(float);
        var samplesRequested = bytesRequested / bytesPerSample;

        // Warn if PA requests more than our buffer (shouldn't happen with larger buffer)
        if (samplesRequested > sampleBuffer.Length)
        {
            _logger.LogWarning(
                "PA requested {Requested} samples but buffer is {BufferSize}. Capping request.",
                samplesRequested, sampleBuffer.Length);
            samplesRequested = sampleBuffer.Length;
        }

        // Read from the sample source (BufferedAudioSampleSource).
        // This may return 0 if the SDK's scheduled start time hasn't been reached yet,
        // or if the buffer is empty. In either case, we write silence.
        var samplesRead = source.Read(sampleBuffer, 0, samplesRequested);

        if (samplesRead == 0)
        {
            WriteSilence(stream, nbytes);
            _silenceWriteCount++;
            _zeroReadCount++;

            // Log periodically when Read() returns 0 - indicates SDK hasn't started releasing samples
            if (_callbackCount % DiagnosticLogInterval == 0)
            {
                var elapsed = (DateTime.UtcNow - _playbackStartTime).TotalMilliseconds;
                _logger.LogDebug(
                    "Read returned 0: elapsed={Elapsed:F0}ms, callbacks={Callbacks}, zeroReads={ZeroReads}, latency={Latency}ms",
                    elapsed, _callbackCount, _zeroReadCount, OutputLatencyMs);
            }
            return;
        }

        // Log first successful audio read - important milestone for debugging startup
        if (!_hasLoggedFirstAudio)
        {
            _hasLoggedFirstAudio = true;
            var elapsed = (DateTime.UtcNow - _playbackStartTime).TotalMilliseconds;
            _logger.LogInformation(
                "First audio samples received: elapsed={Elapsed:F0}ms, callbacks={Callbacks}, " +
                "silenceWrites={Silence}, zeroReads={ZeroReads}, latency={Latency}ms",
                elapsed, _callbackCount, _silenceWriteCount, _zeroReadCount, OutputLatencyMs);
        }

        // Apply software volume and mute
        var vol = IsMuted ? 0f : Volume;
        for (int i = 0; i < samplesRead; i++)
        {
            sampleBuffer[i] *= vol;
        }

        // Convert float samples to bytes for pa_stream_write
        Buffer.BlockCopy(sampleBuffer, 0, byteBuffer, 0, samplesRead * bytesPerSample);

        // Write audio data to PulseAudio stream.
        // SeekMode.Relative: append to current write position (normal streaming mode).
        // freeCallback=null: we manage our own buffer, PA should not free it.
        unsafe
        {
            fixed (byte* ptr = byteBuffer)
            {
                var result = StreamWrite(
                    stream,
                    (IntPtr)ptr,
                    (UIntPtr)(samplesRead * bytesPerSample),
                    IntPtr.Zero,  // freeCallback - null, we manage buffer
                    0,            // offset - 0 for relative seek
                    SeekMode.Relative);

                if (result < 0)
                {
                    _logger.LogDebug("PulseAudio stream write failed");
                }
            }
        }
    }

    /// <summary>
    /// Called when an underflow occurs (buffer ran out of data).
    /// </summary>
    private void OnUnderflow(IntPtr stream, IntPtr userdata)
    {
        _underflowCount++;

        // First few underflows at startup are expected while SDK buffers fill
        if (_underflowCount == 1)
        {
            var elapsed = (DateTime.UtcNow - _playbackStartTime).TotalMilliseconds;
            _logger.LogDebug(
                "First underflow at {Elapsed:F0}ms after play. callbacks={Callbacks}, zeroReads={ZeroReads}",
                elapsed, _callbackCount, _zeroReadCount);
        }
        else if (_underflowCount == UnderflowWarningThreshold)
        {
            var elapsed = (DateTime.UtcNow - _playbackStartTime).TotalMilliseconds;
            _logger.LogWarning(
                "Audio underflow detected ({Count} times at {Elapsed:F0}ms). " +
                "callbacks={Callbacks}, zeroReads={ZeroReads}, latency={Latency}ms",
                _underflowCount, elapsed, _callbackCount, _zeroReadCount, OutputLatencyMs);
        }
        else if (_underflowCount % 100 == 0)
        {
            _logger.LogDebug("Underflow count: {Count}", _underflowCount);
        }
    }

    /// <summary>
    /// Write silence to the stream.
    /// Uses pre-allocated buffer to avoid GC pressure in the audio callback.
    /// </summary>
    private void WriteSilence(IntPtr stream, UIntPtr nbytes)
    {
        var bytesRequested = (int)(ulong)nbytes;

        // Resize silence buffer if needed (rare - only if PA requests more than expected)
        if (_silenceBuffer.Length < bytesRequested)
        {
            _silenceBuffer = new byte[bytesRequested];
        }

        // Buffer is already zeroed (silence) - just write it
        unsafe
        {
            fixed (byte* ptr = _silenceBuffer)
            {
                StreamWrite(stream, (IntPtr)ptr, nbytes, IntPtr.Zero, 0, SeekMode.Relative);
            }
        }
    }

    /// <summary>
    /// Clean up all PulseAudio resources.
    /// </summary>
    private void CleanupResources()
    {
        _contextReady = false;
        _streamReady = false;

        if (_stream != IntPtr.Zero)
        {
            if (_mainloop != IntPtr.Zero)
            {
                ThreadedMainloopLock(_mainloop);
                try
                {
                    StreamDisconnect(_stream);
                }
                finally
                {
                    ThreadedMainloopUnlock(_mainloop);
                }
            }
            StreamUnref(_stream);
            _stream = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero)
        {
            if (_mainloop != IntPtr.Zero)
            {
                ThreadedMainloopLock(_mainloop);
                try
                {
                    ContextDisconnect(_context);
                }
                finally
                {
                    ThreadedMainloopUnlock(_mainloop);
                }
            }
            ContextUnref(_context);
            _context = IntPtr.Zero;
        }

        if (_mainloop != IntPtr.Zero)
        {
            ThreadedMainloopStop(_mainloop);
            ThreadedMainloopFree(_mainloop);
            _mainloop = IntPtr.Zero;
        }

        // Clear callback references (safe to do after mainloop is stopped)
        _contextStateCallback = null;
        _streamStateCallback = null;
        _writeCallback = null;
        _underflowCallback = null;

        _sampleBuffer = null;
        _byteBuffer = null;
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

        Stop();

        lock (_lock)
        {
            CleanupResources();
        }

        _logger.LogInformation("PulseAudio player disposed");
        return ValueTask.CompletedTask;
    }
}
