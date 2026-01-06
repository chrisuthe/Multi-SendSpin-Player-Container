using MultiRoomAudio.Models;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio.Alsa;

/// <summary>
/// IAudioPlayer implementation using direct ALSA libasound API.
/// Designed for Docker environments with raw ALSA device access.
/// </summary>
/// <remarks>
/// This player uses ALSA's PCM API directly via P/Invoke, allowing playback
/// to any ALSA device including software-defined devices from asound.conf.
/// Audio data is written on a dedicated thread using blocking I/O.
/// </remarks>
public class AlsaPlayer : IAudioPlayer
{
    private readonly ILogger<AlsaPlayer> _logger;
    private readonly object _lock = new();

    private IntPtr _pcmHandle = IntPtr.Zero;
    private IAudioSampleSource? _sampleSource;
    private AudioFormat? _currentFormat;
    private string _deviceName;
    private bool _disposed;

    private Thread? _playbackThread;
    private CancellationTokenSource? _playbackCts;
    private volatile bool _isPlaying;
    private volatile bool _isPaused;

    // Output format configuration
    private AudioOutputFormat? _outputFormat;
    private AlsaNative.Format _alsaFormat = AlsaNative.Format.FLOAT_LE;
    private int _bytesPerSample = 4;
    private bool _needsBitDepthConversion;

    // Pre-allocated buffers for the playback thread
    private float[]? _sampleBuffer;
    private byte[]? _byteBuffer;

    /// <summary>
    /// Target latency in microseconds (50ms = 50000μs).
    /// </summary>
    private const uint TargetLatencyUs = 50_000;

    /// <summary>
    /// Frames per write operation. At 48kHz, 1024 frames = ~21ms of audio.
    /// </summary>
    private const int FramesPerWrite = 1024;

    /// <summary>
    /// Number of consecutive recovery failures before attempting device reconnection.
    /// </summary>
    private const int MaxRecoveryFailures = 5;

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
    /// Initializes a new instance of the AlsaPlayer.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="deviceName">
    /// ALSA device name. Can be:
    /// - "default" or null for default device
    /// - "hw:0,0" for direct hardware access
    /// - "plughw:0,0" for plugin-wrapped hardware
    /// - Custom device from asound.conf (e.g., "zone1")
    /// </param>
    /// <param name="outputFormat">
    /// Optional output format configuration. If null, uses 32-bit float output.
    /// Specify sample rate and bit depth for hi-res output (e.g., 192kHz/24-bit).
    /// </param>
    public AlsaPlayer(ILogger<AlsaPlayer> logger, string? deviceName = null, AudioOutputFormat? outputFormat = null)
    {
        _logger = logger;
        _deviceName = deviceName ?? "default";
        _outputFormat = outputFormat;

        // Determine ALSA format based on requested bit depth
        if (outputFormat != null)
        {
            (_alsaFormat, _bytesPerSample, _needsBitDepthConversion) = outputFormat.BitDepth switch
            {
                16 => (AlsaNative.Format.S16_LE, 2, true),
                24 => (AlsaNative.Format.S24_LE, 4, true),  // 24-bit in 4-byte container
                32 => (AlsaNative.Format.FLOAT_LE, 4, false),
                _ => (AlsaNative.Format.FLOAT_LE, 4, false)
            };
        }
    }

    /// <summary>
    /// Gets the configured output format, or null if using default.
    /// </summary>
    public AudioOutputFormat? OutputFormat => _outputFormat;

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            try
            {
                // Determine actual sample rate to use (output format overrides incoming)
                var actualSampleRate = _outputFormat?.SampleRate ?? format.SampleRate;

                _logger.LogInformation(
                    "Initializing ALSA player: {SampleRate}Hz, {Channels}ch, {BitDepth}-bit, device: {Device}",
                    actualSampleRate, format.Channels,
                    _outputFormat?.BitDepth ?? 32, _deviceName);

                // Open the ALSA device
                var result = AlsaNative.Open(
                    out _pcmHandle,
                    _deviceName,
                    AlsaNative.StreamType.Playback,
                    0);  // Blocking mode

                if (result < 0)
                {
                    var errorMsg = AlsaNative.GetErrorMessage(result);
                    throw new InvalidOperationException(
                        $"Failed to open ALSA device '{_deviceName}': {errorMsg}");
                }

                // Configure PCM parameters using simplified API
                result = AlsaNative.SetParams(
                    _pcmHandle,
                    _alsaFormat,
                    AlsaNative.Access.RwInterleaved,
                    (uint)format.Channels,
                    (uint)actualSampleRate,
                    1,  // Allow software resampling
                    TargetLatencyUs);

                if (result < 0)
                {
                    var errorMsg = AlsaNative.GetErrorMessage(result);
                    AlsaNative.Close(_pcmHandle);
                    _pcmHandle = IntPtr.Zero;
                    throw new InvalidOperationException(
                        $"Failed to set ALSA parameters: {errorMsg}");
                }

                _currentFormat = format;

                // Calculate latency from target
                OutputLatencyMs = (int)(TargetLatencyUs / 1000);

                // Pre-allocate buffers
                var samplesPerWrite = FramesPerWrite * format.Channels;
                _sampleBuffer = new float[samplesPerWrite];
                _byteBuffer = new byte[samplesPerWrite * _bytesPerSample];

                SetState(AudioPlayerState.Stopped);

                _logger.LogInformation(
                    "ALSA player initialized. Device: {Device}, Format: {Format}, Rate: {Rate}Hz, Latency: {Latency}ms",
                    _deviceName, AlsaNative.GetFormatName(_alsaFormat), actualSampleRate, OutputLatencyMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ALSA player");
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
            if (_pcmHandle == IntPtr.Zero)
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

            // Prepare the PCM device
            var result = AlsaNative.Prepare(_pcmHandle);
            if (result < 0)
            {
                _logger.LogWarning("Failed to prepare ALSA device: {Error}",
                    AlsaNative.GetErrorMessage(result));
            }

            // Start playback thread
            _playbackCts = new CancellationTokenSource();
            _isPlaying = true;
            _isPaused = false;

            _playbackThread = new Thread(PlaybackLoop)
            {
                Name = "ALSA-Playback",
                IsBackground = true
            };

            try
            {
                _playbackThread.Priority = ThreadPriority.AboveNormal;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Could not set thread priority to AboveNormal. Audio may have higher latency.");
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
        IntPtr handleToDrop = IntPtr.Zero;

        lock (_lock)
        {
            if (!_isPlaying)
                return;

            _isPlaying = false;
            _isPaused = false;
            _playbackCts?.Cancel();
            threadToJoin = _playbackThread;
            handleToDrop = _pcmHandle;
        }

        // Call Drop() BEFORE waiting for thread to unblock any blocking WriteInterleaved call.
        // This allows the playback thread to exit promptly instead of waiting for the
        // blocking write to complete or timeout.
        if (handleToDrop != IntPtr.Zero)
        {
            AlsaNative.Drop(handleToDrop);
        }

        // Wait for thread outside lock
        if (threadToJoin != null)
        {
            if (!threadToJoin.Join(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning(
                    "Playback thread did not exit within 5 seconds");
            }
        }

        lock (_lock)
        {
            _playbackThread = null;
            _playbackCts?.Dispose();
            _playbackCts = null;

            SetState(AudioPlayerState.Stopped);
            _logger.LogInformation("Playback stopped");
        }
    }

    public async Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Switching ALSA device to: {Device}", deviceId ?? "default");

        var wasPlaying = State == AudioPlayerState.Playing;
        var savedSource = _sampleSource;
        var savedFormat = _currentFormat;
        var savedDeviceName = _deviceName;
        IntPtr oldHandle = IntPtr.Zero;

        // Stop current playback
        Stop();

        // Save the old handle
        lock (_lock)
        {
            oldHandle = _pcmHandle;
            _pcmHandle = IntPtr.Zero;
        }

        // Update device name
        _deviceName = deviceId ?? "default";

        // Reinitialize with new device
        if (savedFormat != null)
        {
            try
            {
                await InitializeAsync(savedFormat, cancellationToken);

                // Close old handle after successful initialization
                if (oldHandle != IntPtr.Zero)
                {
                    AlsaNative.Close(oldHandle);
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

                // Restore old handle on failure
                lock (_lock)
                {
                    if (_pcmHandle == IntPtr.Zero && oldHandle != IntPtr.Zero)
                    {
                        _pcmHandle = oldHandle;
                        oldHandle = IntPtr.Zero;
                        _deviceName = savedDeviceName;
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
                    AlsaNative.Close(oldHandle);
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
                    Thread.Sleep(10);
                    continue;
                }

                var source = _sampleSource;
                var buffer = _sampleBuffer;
                var byteBuffer = _byteBuffer;
                var format = _currentFormat;

                if (source == null || buffer == null || byteBuffer == null || format == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Capture handle under lock to prevent race condition with Stop/Dispose
                IntPtr pcmHandle;
                lock (_lock)
                {
                    pcmHandle = _pcmHandle;
                }

                // Check if handle is valid, attempt reconnect if needed
                if (pcmHandle == IntPtr.Zero)
                {
                    _logger.LogWarning("ALSA handle is null - attempting reconnection...");
                    if (!TryReconnect())
                    {
                        _logger.LogError("ALSA reconnection failed - stopping playback");
                        OnError("ALSA device lost and could not reconnect", null);
                        break;
                    }
                    consecutiveFailures = 0;
                    continue;
                }

                // Read samples from source
                var samplesRead = source.Read(buffer, 0, buffer.Length);

                if (samplesRead == 0)
                {
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

                // Calculate frames
                var frames = samplesRead / format.Channels;

                // Write to ALSA using captured handle
                bool writeSuccess = false;
                unsafe
                {
                    fixed (byte* ptr = byteBuffer)
                    {
                        var written = AlsaNative.WriteInterleaved(
                            pcmHandle,
                            (IntPtr)ptr,
                            (nuint)frames);

                        if (written < 0)
                        {
                            // Handle errors
                            var errorCode = (int)written;
                            var recovered = HandlePlaybackError(pcmHandle, errorCode);

                            if (!recovered)
                            {
                                consecutiveFailures++;

                                if (consecutiveFailures >= MaxRecoveryFailures)
                                {
                                    _logger.LogWarning(
                                        "ALSA recovery failed {Count} consecutive times. Attempting device reconnection...",
                                        consecutiveFailures);

                                    if (!TryReconnect())
                                    {
                                        _logger.LogError("ALSA reconnection failed - stopping playback");
                                        OnError($"ALSA device lost: {AlsaNative.GetErrorMessage(errorCode)}", null);
                                        break;
                                    }
                                    consecutiveFailures = 0;
                                }
                                else
                                {
                                    Thread.Sleep(10);
                                }
                            }
                            else
                            {
                                // Recovery succeeded - reset failure counter
                                consecutiveFailures = 0;
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
    /// Handles ALSA playback errors with recovery attempts.
    /// </summary>
    /// <param name="pcmHandle">The PCM handle to use for recovery (captured to avoid race conditions).</param>
    /// <param name="errorCode">The error code from the failed operation.</param>
    /// <returns>True if recovery succeeded, false if reconnection is needed.</returns>
    private bool HandlePlaybackError(IntPtr pcmHandle, int errorCode)
    {
        // Try to recover from common errors
        if (errorCode == AlsaNative.EPIPE)
        {
            // Underrun - buffer ran empty
            _logger.LogDebug("ALSA underrun occurred, recovering...");
            var recoverResult = AlsaNative.Recover(pcmHandle, errorCode, 1);
            if (recoverResult < 0)
            {
                _logger.LogWarning("Failed to recover from underrun: {Error}",
                    AlsaNative.GetErrorMessage(recoverResult));
                return false;
            }
            return true;
        }
        else if (errorCode == AlsaNative.ESTRPIPE)
        {
            // Suspend - device was suspended
            _logger.LogDebug("ALSA device suspended, attempting resume...");

            // Try to resume
            int resumeResult;
            while ((resumeResult = AlsaNative.Resume(pcmHandle)) == AlsaNative.EAGAIN)
            {
                Thread.Sleep(10);
            }

            if (resumeResult < 0)
            {
                // Resume failed, try full recovery
                var recoverResult = AlsaNative.Recover(pcmHandle, errorCode, 1);
                if (recoverResult < 0)
                {
                    _logger.LogWarning("Failed to recover from suspend: {Error}",
                        AlsaNative.GetErrorMessage(recoverResult));
                    return false;
                }
            }
            return true;
        }
        else if (errorCode == AlsaNative.EAGAIN)
        {
            // Non-blocking would return this - just wait
            Thread.Sleep(1);
            return true;
        }
        else
        {
            // Other error - try generic recovery
            _logger.LogWarning("ALSA write error: {Error}",
                AlsaNative.GetErrorMessage(errorCode));
            var recoverResult = AlsaNative.Recover(pcmHandle, errorCode, 1);
            if (recoverResult < 0)
            {
                _logger.LogError("Failed to recover from error: {Error}",
                    AlsaNative.GetErrorMessage(recoverResult));
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Attempts to reconnect to the ALSA device with exponential backoff.
    /// Called when the playback loop detects consecutive recovery failures.
    /// </summary>
    /// <returns>True if reconnection succeeded, false otherwise.</returns>
    private bool TryReconnect()
    {
        if (_currentFormat == null)
        {
            _logger.LogError("Cannot reconnect - no format configured");
            return false;
        }

        _logger.LogWarning("ALSA device lost - attempting to reconnect to '{Device}'...", _deviceName);

        // Close the dead handle if it exists
        lock (_lock)
        {
            if (_pcmHandle != IntPtr.Zero)
            {
                try
                {
                    AlsaNative.Close(_pcmHandle);
                }
                catch
                {
                    // Ignore errors when closing dead handle
                }
                _pcmHandle = IntPtr.Zero;
            }
        }

        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            // Calculate delay with exponential backoff
            var delay = Math.Min(ReconnectBaseDelayMs * (1 << (attempt - 1)), ReconnectMaxDelayMs);

            _logger.LogInformation(
                "ALSA reconnect attempt {Attempt}/{MaxAttempts} in {Delay}ms...",
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
                // Try to open the device
                var result = AlsaNative.Open(
                    out var newHandle,
                    _deviceName,
                    AlsaNative.StreamType.Playback,
                    0);

                if (result < 0)
                {
                    _logger.LogWarning(
                        "ALSA reconnect attempt {Attempt} failed to open device: {Error}",
                        attempt, AlsaNative.GetErrorMessage(result));
                    continue;
                }

                // Use configured output sample rate if specified
                var actualSampleRate = _outputFormat?.SampleRate ?? _currentFormat.SampleRate;

                // Configure PCM parameters
                result = AlsaNative.SetParams(
                    newHandle,
                    _alsaFormat,
                    AlsaNative.Access.RwInterleaved,
                    (uint)_currentFormat.Channels,
                    (uint)actualSampleRate,
                    1,
                    TargetLatencyUs);

                if (result < 0)
                {
                    _logger.LogWarning(
                        "ALSA reconnect attempt {Attempt} failed to set params: {Error}",
                        attempt, AlsaNative.GetErrorMessage(result));
                    AlsaNative.Close(newHandle);
                    continue;
                }

                // Prepare the device
                result = AlsaNative.Prepare(newHandle);
                if (result < 0)
                {
                    _logger.LogWarning(
                        "ALSA reconnect attempt {Attempt} failed to prepare: {Error}",
                        attempt, AlsaNative.GetErrorMessage(result));
                    AlsaNative.Close(newHandle);
                    continue;
                }

                // Success!
                lock (_lock)
                {
                    _pcmHandle = newHandle;
                }

                _logger.LogInformation(
                    "ALSA reconnected successfully on attempt {Attempt}",
                    attempt);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ALSA reconnect attempt {Attempt} threw exception",
                    attempt);
            }
        }

        _logger.LogError(
            "ALSA reconnection failed after {MaxAttempts} attempts",
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

        // Stop playback
        Stop();

        IntPtr handleToClose;
        lock (_lock)
        {
            handleToClose = _pcmHandle;
            _pcmHandle = IntPtr.Zero;

            _sampleBuffer = null;
            _byteBuffer = null;
        }

        // Close the handle outside the lock
        if (handleToClose != IntPtr.Zero)
        {
            AlsaNative.Close(handleToClose);
        }

        _logger.LogInformation("ALSA player disposed");
        return ValueTask.CompletedTask;
    }
}
