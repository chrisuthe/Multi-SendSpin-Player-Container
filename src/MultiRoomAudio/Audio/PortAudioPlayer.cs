using PortAudioSharp;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio;

/// <summary>
/// IAudioPlayer implementation using PortAudio for Linux containers.
/// Provides synchronized audio output with hot-switching capability.
/// </summary>
public class PortAudioPlayer : IAudioPlayer
{
    private readonly ILogger<PortAudioPlayer> _logger;
    private readonly object _lock = new();

    private PortAudioSharp.Stream? _stream;
    private IAudioSampleSource? _sampleSource;
    private AudioFormat? _currentFormat;
    private string? _deviceId;
    private bool _disposed;

    // Pre-allocated buffer to avoid allocations in real-time audio callback
    private float[]? _callbackBuffer;
    private int _callbackBufferChannels;

    /// <summary>
    /// Maximum number of audio frames to pre-allocate for the callback buffer.
    /// This value of 4096 frames is chosen because:
    /// 1. It's a power of 2, which aligns well with audio driver buffer sizes
    /// 2. At 48kHz, this represents ~85ms of audio - more than enough for any reasonable
    ///    PortAudio callback request (typically 256-1024 frames)
    /// 3. It balances memory usage (~32KB for stereo float32) against allocation safety
    /// 4. Industry standard buffer size used by many audio applications (JACK, PulseAudio)
    /// </summary>
    private const int MaxCallbackFrames = 4096;

    /// <summary>
    /// Static reference counter for PortAudio initialization.
    /// PortAudio.Initialize() must be called before use, and PortAudio.Terminate()
    /// should only be called when all players are disposed.
    /// </summary>
    private static readonly object _portAudioLock = new();
    private static int _portAudioRefCount = 0;

    /// <summary>
    /// Gets the current state of the audio player.
    /// </summary>
    /// <remarks>
    /// State transitions follow this pattern:
    /// Uninitialized -> Stopped (after InitializeAsync)
    /// Stopped -> Playing (after Play) or Error (on failure)
    /// Playing -> Paused (after Pause) or Stopped (after Stop)
    /// Paused -> Playing (after Play) or Stopped (after Stop)
    /// Any state -> Error (on unrecoverable failure)
    /// </remarks>
    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    // Thread-safe volume and mute state for real-time audio callback access
    private volatile float _volume = 1.0f;
    private volatile bool _isMuted;

    /// <summary>
    /// Gets or sets the playback volume (0.0 to 1.0).
    /// Thread-safe for access from the audio callback.
    /// </summary>
    public float Volume
    {
        get => _volume;
        set => _volume = value;
    }

    /// <summary>
    /// Gets or sets whether playback is muted.
    /// Thread-safe for access from the audio callback.
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    /// <summary>
    /// Gets the actual output latency in milliseconds.
    /// </summary>
    /// <remarks>
    /// This value is determined by PortAudio based on the audio device's default low latency setting.
    /// It represents the time delay between when audio samples are submitted to the driver and when
    /// they are played through the speakers. Used by the SDK for sync calculations.
    /// </remarks>
    public int OutputLatencyMs { get; private set; }

    /// <summary>
    /// Occurs when the player state changes.
    /// </summary>
    /// <remarks>
    /// Subscribers can use this event to update UI or trigger state-dependent logic.
    /// The event is raised on the thread that caused the state change.
    /// </remarks>
    public event EventHandler<AudioPlayerState>? StateChanged;

    /// <summary>
    /// Occurs when an error is encountered during playback operations.
    /// </summary>
    /// <remarks>
    /// Errors do not necessarily indicate a fatal condition. The player may still be usable
    /// depending on the error type. Check the <see cref="State"/> property after receiving an error.
    /// </remarks>
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the <see cref="PortAudioPlayer"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="deviceId">
    /// Optional device identifier. Can be a numeric index or partial device name.
    /// If null or empty, the system default audio output device is used.
    /// </param>
    public PortAudioPlayer(ILogger<PortAudioPlayer> logger, string? deviceId = null)
    {
        _logger = logger;
        _deviceId = deviceId;
    }

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var refCountIncremented = false;
            try
            {
                _logger.LogInformation("Initializing PortAudio player with format: {SampleRate}Hz, {Channels}ch",
                    format.SampleRate, format.Channels);

                // Initialize PortAudio with reference counting (thread-safe)
                lock (_portAudioLock)
                {
                    if (_portAudioRefCount == 0)
                    {
                        PortAudio.Initialize();
                        _logger.LogDebug("PortAudio library initialized");
                    }
                    _portAudioRefCount++;
                    refCountIncremented = true;
                    _logger.LogDebug("PortAudio reference count: {RefCount}", _portAudioRefCount);
                }

                // Find device
                var deviceIndex = FindDeviceIndex(_deviceId);
                var deviceInfo = PortAudio.GetDeviceInfo(deviceIndex);

                _logger.LogInformation("Using audio device: {DeviceName} (index {Index})",
                    deviceInfo.name, deviceIndex);

                // Create stream parameters
                var outputParams = new StreamParameters
                {
                    device = deviceIndex,
                    channelCount = format.Channels,
                    sampleFormat = SampleFormat.Float32,
                    suggestedLatency = deviceInfo.defaultLowOutputLatency
                };

                // Create the stream
                _stream = new PortAudioSharp.Stream(
                    inParams: null,
                    outParams: outputParams,
                    sampleRate: format.SampleRate,
                    framesPerBuffer: 0, // Let PortAudio choose
                    streamFlags: StreamFlags.ClipOff,
                    callback: AudioCallback,
                    userData: IntPtr.Zero
                );

                _currentFormat = format;
                OutputLatencyMs = (int)(outputParams.suggestedLatency * 1000);

                // Pre-allocate callback buffer for real-time audio thread
                _callbackBufferChannels = format.Channels;
                _callbackBuffer = new float[MaxCallbackFrames * format.Channels];

                SetState(AudioPlayerState.Stopped);
                _logger.LogInformation("PortAudio player initialized. Latency: {Latency}ms", OutputLatencyMs);
            }
            catch (Exception ex)
            {
                // Decrement ref count if we incremented it before failure
                if (refCountIncremented)
                {
                    lock (_portAudioLock)
                    {
                        _portAudioRefCount--;
                        _logger.LogDebug("PortAudio reference count decremented on init failure: {RefCount}", _portAudioRefCount);
                        if (_portAudioRefCount < 0)
                        {
                            _portAudioRefCount = 0;
                        }
                    }
                }

                _logger.LogError(ex, "Failed to initialize PortAudio player");
                SetState(AudioPlayerState.Error);
                OnError("Initialization failed", ex);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the audio sample source that provides audio data for playback.
    /// </summary>
    /// <param name="source">
    /// The audio sample source to read from. Must match the format specified in
    /// <see cref="InitializeAsync"/>. Can be hot-swapped during playback.
    /// </param>
    /// <remarks>
    /// This method is thread-safe and can be called while audio is playing.
    /// The new source will be used starting with the next audio callback.
    /// </remarks>
    public void SetSampleSource(IAudioSampleSource source)
    {
        lock (_lock)
        {
            _sampleSource = source;
            _logger.LogDebug("Sample source set");
        }
    }

    /// <summary>
    /// Starts or resumes audio playback.
    /// </summary>
    /// <remarks>
    /// If the player is in <see cref="AudioPlayerState.Stopped"/> or <see cref="AudioPlayerState.Paused"/>
    /// state, this method starts the audio stream. If no sample source is set, silence will be output.
    /// Raises <see cref="ErrorOccurred"/> if the stream fails to start.
    /// </remarks>
    public void Play()
    {
        lock (_lock)
        {
            if (_stream == null)
            {
                _logger.LogWarning("Cannot play - stream not initialized");
                return;
            }

            try
            {
                _stream.Start();
                SetState(AudioPlayerState.Playing);
                _logger.LogInformation("Playback started");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start playback");
                OnError("Playback start failed", ex);
            }
        }
    }

    /// <summary>
    /// Pauses audio playback without releasing resources.
    /// </summary>
    /// <remarks>
    /// The audio stream is stopped but remains initialized. Call <see cref="Play"/> to resume.
    /// This is more efficient than <see cref="Stop"/> when playback will resume shortly,
    /// as it maintains buffer state and device connection.
    /// </remarks>
    public void Pause()
    {
        lock (_lock)
        {
            if (_stream == null)
                return;

            try
            {
                _stream.Stop();
                SetState(AudioPlayerState.Paused);
                _logger.LogInformation("Playback paused");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause playback");
                OnError("Pause failed", ex);
            }
        }
    }

    /// <summary>
    /// Stops audio playback.
    /// </summary>
    /// <remarks>
    /// The audio stream is stopped but remains initialized for future playback.
    /// This does not dispose resources - call <see cref="DisposeAsync"/> to fully clean up.
    /// </remarks>
    public void Stop()
    {
        lock (_lock)
        {
            if (_stream == null)
                return;

            try
            {
                if (_stream.IsActive)
                {
                    _stream.Stop();
                }
                SetState(AudioPlayerState.Stopped);
                _logger.LogInformation("Playback stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop playback");
                OnError("Stop failed", ex);
            }
        }
    }

    public async Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Switching audio device to: {DeviceId}", deviceId ?? "default");

        var wasPlaying = State == AudioPlayerState.Playing;
        var savedSource = _sampleSource;
        var savedFormat = _currentFormat;
        var oldDeviceId = _deviceId;

        // Save references to old stream for rollback on failure
        PortAudioSharp.Stream? oldStream;
        float[]? oldCallbackBuffer;
        int oldCallbackBufferChannels;

        lock (_lock)
        {
            oldStream = _stream;
            oldCallbackBuffer = _callbackBuffer;
            oldCallbackBufferChannels = _callbackBufferChannels;
        }

        // Stop playback but don't dispose the stream yet
        Stop();

        // Update device ID for new initialization
        _deviceId = deviceId;

        if (savedFormat != null)
        {
            try
            {
                // Clear current stream reference so InitializeAsync creates a new one
                lock (_lock)
                {
                    _stream = null;
                    _callbackBuffer = null;
                }

                await InitializeAsync(savedFormat, cancellationToken);

                // Success - now dispose the old stream
                oldStream?.Dispose();

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
                // Initialization failed - restore old stream to keep player usable
                _logger.LogError(ex, "Device switch failed, restoring previous device");

                lock (_lock)
                {
                    _stream = oldStream;
                    _callbackBuffer = oldCallbackBuffer;
                    _callbackBufferChannels = oldCallbackBufferChannels;
                }

                _deviceId = oldDeviceId;
                SetState(AudioPlayerState.Stopped);

                // Re-attach sample source
                if (savedSource != null)
                {
                    SetSampleSource(savedSource);
                }

                OnError("Device switch failed", ex);
                throw;
            }
        }
        else
        {
            // No format saved, just dispose old stream
            oldStream?.Dispose();
            _logger.LogInformation("Device switch complete (no format to reinitialize)");
        }
    }

    private StreamCallbackResult AudioCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        try
        {
            var channels = _currentFormat?.Channels ?? 2;
            var samplesNeeded = (int)(frameCount * channels);

            if (_sampleSource == null || IsMuted)
            {
                // Output silence
                unsafe
                {
                    var buffer = (float*)output;
                    for (int i = 0; i < samplesNeeded; i++)
                    {
                        buffer[i] = 0f;
                    }
                }
                return StreamCallbackResult.Continue;
            }

            // Check pre-allocated buffer is large enough - DO NOT allocate in real-time callback
            if (_callbackBuffer == null || _callbackBuffer.Length < samplesNeeded)
            {
                // Buffer too small - this should never happen with 4096 frame pre-allocation
                // Output silence to avoid allocation in real-time context
                _logger.LogWarning("Audio callback buffer too small: need {Needed}, have {Have}. Outputting silence.",
                    samplesNeeded, _callbackBuffer?.Length ?? 0);
                unsafe
                {
                    var buffer = (float*)output;
                    for (int i = 0; i < samplesNeeded; i++)
                    {
                        buffer[i] = 0f;
                    }
                }
                return StreamCallbackResult.Continue;
            }

            // Read samples from source into pre-allocated buffer
            var read = _sampleSource.Read(_callbackBuffer, 0, samplesNeeded);

            // Apply volume and copy to output
            unsafe
            {
                var buffer = (float*)output;
                var vol = Volume;
                for (int i = 0; i < read; i++)
                {
                    buffer[i] = _callbackBuffer[i] * vol;
                }
                // Fill remaining with silence
                for (int i = read; i < samplesNeeded; i++)
                {
                    buffer[i] = 0f;
                }
            }

            return StreamCallbackResult.Continue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in audio callback");
            return StreamCallbackResult.Continue;
        }
    }

    private int FindDeviceIndex(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return PortAudio.DefaultOutputDevice;
        }

        // Try to parse as index
        if (int.TryParse(deviceId, out var index))
        {
            if (index >= 0 && index < PortAudio.DeviceCount)
            {
                var info = PortAudio.GetDeviceInfo(index);
                if (info.maxOutputChannels > 0)
                {
                    return index;
                }
            }
        }

        // Search by name
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels > 0 &&
                info.name.Contains(deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        _logger.LogWarning("Device '{DeviceId}' not found, using default", deviceId);
        return PortAudio.DefaultOutputDevice;
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

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            // Check disposed inside lock to avoid race condition
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_stream != null)
                {
                    if (_stream.IsActive)
                    {
                        _stream.Stop();
                    }
                    _stream.Dispose();
                    _stream = null;
                }

                // Decrement reference count but DON'T terminate PortAudio
                // PortAudio should stay initialized for device enumeration to work
                // The library will be cleaned up when the process exits
                lock (_portAudioLock)
                {
                    _portAudioRefCount--;
                    _logger.LogDebug("PortAudio reference count: {RefCount}", _portAudioRefCount);
                    if (_portAudioRefCount < 0)
                    {
                        _portAudioRefCount = 0; // Ensure non-negative
                    }
                    // Note: We intentionally don't call PortAudio.Terminate() here
                    // as it breaks device enumeration for other players/API calls
                }

                _logger.LogInformation("PortAudio player disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing PortAudio player");
            }
        }

        await Task.CompletedTask;
    }
}
