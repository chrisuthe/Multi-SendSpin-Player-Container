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

    /// <summary>
    /// Static reference counter for PortAudio initialization.
    /// PortAudio.Initialize() must be called before use, and PortAudio.Terminate()
    /// should only be called when all players are disposed.
    /// </summary>
    private static readonly object _portAudioLock = new();
    private static int _portAudioRefCount = 0;

    // State
    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;
    public float Volume { get; set; } = 1.0f;
    public bool IsMuted { get; set; }
    public int OutputLatencyMs { get; private set; }

    // Events
    public event EventHandler<AudioPlayerState>? StateChanged;
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    public PortAudioPlayer(ILogger<PortAudioPlayer> logger, string? deviceId = null)
    {
        _logger = logger;
        _deviceId = deviceId;
    }

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
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

                SetState(AudioPlayerState.Stopped);
                _logger.LogInformation("PortAudio player initialized. Latency: {Latency}ms", OutputLatencyMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize PortAudio player");
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

    public void Pause()
    {
        lock (_lock)
        {
            if (_stream == null) return;

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

    public void Stop()
    {
        lock (_lock)
        {
            if (_stream == null) return;

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

        // Stop and dispose current stream
        Stop();

        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
        }

        // Reinitialize with new device
        _deviceId = deviceId;

        if (savedFormat != null)
        {
            await InitializeAsync(savedFormat, cancellationToken);

            if (savedSource != null)
            {
                SetSampleSource(savedSource);
            }

            if (wasPlaying)
            {
                Play();
            }
        }

        _logger.LogInformation("Device switch complete");
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
            if (_sampleSource == null || IsMuted)
            {
                // Output silence
                unsafe
                {
                    var buffer = (float*)output;
                    var channels = _currentFormat?.Channels ?? 2;
                    for (int i = 0; i < frameCount * channels; i++)
                    {
                        buffer[i] = 0f;
                    }
                }
                return StreamCallbackResult.Continue;
            }

            // Read samples from source and apply volume
            var samples = new float[frameCount * (_currentFormat?.Channels ?? 2)];
            var read = _sampleSource.Read(samples, 0, samples.Length);

            unsafe
            {
                var buffer = (float*)output;
                for (int i = 0; i < read; i++)
                {
                    buffer[i] = samples[i] * Volume;
                }
                // Fill remaining with silence
                for (int i = read; i < samples.Length; i++)
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
        if (_disposed) return;

        lock (_lock)
        {
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

                // Decrement reference count and terminate only when last player is disposed
                lock (_portAudioLock)
                {
                    _portAudioRefCount--;
                    _logger.LogDebug("PortAudio reference count: {RefCount}", _portAudioRefCount);

                    if (_portAudioRefCount <= 0)
                    {
                        _portAudioRefCount = 0; // Ensure non-negative
                        PortAudio.Terminate();
                        _logger.LogDebug("PortAudio library terminated");
                    }
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
