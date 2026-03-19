using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio.Mock;

/// <summary>
/// Mock audio player that discards audio samples.
/// Used when MOCK_HARDWARE is enabled for testing without real audio output.
/// Implements the full IAudioPlayer interface from SendSpin.SDK.
/// </summary>
public class MockAudioPlayer : IAudioPlayer
{
    private readonly ILogger<MockAudioPlayer> _logger;
    private readonly string _deviceName;
    private IAudioSampleSource? _sampleSource;
    private AudioFormat? _currentFormat;
    private volatile bool _disposed;
    private Timer? _playbackTimer;

    public MockAudioPlayer(ILogger<MockAudioPlayer> logger, string? deviceName)
    {
        _logger = logger;
        _deviceName = deviceName ?? "default";
    }

    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    public float Volume { get; set; } = 1.0f;

    public bool IsMuted { get; set; }

    public int OutputLatencyMs { get; private set; } = 50;

    public event EventHandler<AudioPlayerState>? StateChanged;
#pragma warning disable CS0067 // Event is never used (required by IAudioPlayer interface)
    public event EventHandler<AudioPlayerError>? ErrorOccurred;
#pragma warning restore CS0067

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _currentFormat = format;
        State = AudioPlayerState.Stopped;
        StateChanged?.Invoke(this, State);

        _logger.LogInformation(
            "Mock player initialized: {SampleRate}Hz, {Channels}ch, device: {Device}",
            format.SampleRate, format.Channels, _deviceName);

        return Task.CompletedTask;
    }

    public void SetSampleSource(IAudioSampleSource source)
    {
        _sampleSource = source;
        _logger.LogDebug("Sample source set for mock player");
    }

    public void Play()
    {
        if (State == AudioPlayerState.Playing)
            return;

        State = AudioPlayerState.Playing;
        StateChanged?.Invoke(this, State);

        // Start a timer to simulate reading samples (to keep the SDK happy)
        _playbackTimer?.Dispose();
        _playbackTimer = new Timer(SimulatePlayback, null, 0, 20);

        _logger.LogInformation("Mock player started for {Device}", _deviceName);
    }

    public void Pause()
    {
        if (State != AudioPlayerState.Playing)
            return;

        _playbackTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        State = AudioPlayerState.Paused;
        StateChanged?.Invoke(this, State);

        _logger.LogInformation("Mock player paused for {Device}", _deviceName);
    }

    public void Stop()
    {
        _playbackTimer?.Dispose();
        _playbackTimer = null;

        State = AudioPlayerState.Stopped;
        StateChanged?.Invoke(this, State);

        _logger.LogDebug("Mock player stopped for {Device}", _deviceName);
    }

    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock player switching device to: {Device}", deviceId ?? "default");
        return Task.CompletedTask;
    }

    private void SimulatePlayback(object? state)
    {
        if (_disposed || State != AudioPlayerState.Playing || _sampleSource == null)
            return;

        // Read and discard samples to keep the SDK buffer flowing
        var buffer = new float[1024];
        try
        {
            var read = _sampleSource.Read(buffer, 0, buffer.Length);
            // Samples are discarded - this is a null sink
        }
        catch
        {
            // Ignore errors in mock player
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        Stop();

        _logger.LogDebug("Mock player disposed for {Device}", _deviceName);
        return ValueTask.CompletedTask;
    }
}
