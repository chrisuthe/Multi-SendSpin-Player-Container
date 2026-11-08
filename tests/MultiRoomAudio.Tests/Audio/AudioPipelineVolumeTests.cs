using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Xunit;

namespace MultiRoomAudio.Tests.Audio;

/// <summary>
/// Pins down the SDK behaviour the container relies on for volume (#305): the pipeline stores the
/// level passed to <see cref="IAudioPipeline.SetVolume"/> - even while idle - and re-applies it to
/// the player on every stream start. <c>PlayerManagerService</c> routes all volume writes through
/// the pipeline for exactly this reason, so an SDK upgrade that stops re-applying it would bring
/// the post-pause volume jump back and must fail here first.
/// </summary>
public class AudioPipelineVolumeTests
{
    private static readonly AudioFormat Format = new() { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 };

    private sealed class Harness
    {
        public List<FakePlayer> Players { get; } = new();
        public AudioPipeline Pipeline { get; }

        public Harness()
        {
            Pipeline = new AudioPipeline(
                NullLogger<AudioPipeline>.Instance,
                new FakeDecoderFactory(),
                new FakeClock(),
                (_, _) => new RecordingTimedAudioBuffer(Format),
                () =>
                {
                    var player = new FakePlayer();
                    Players.Add(player);
                    return player;
                },
                (buffer, _) => new FakeSampleSource(buffer.Format),
                waitForConvergence: false);
        }

        public FakePlayer CurrentPlayer => Players[^1];
    }

    [Fact]
    public async Task VolumeSetWhileIdle_IsAppliedOnStreamStart()
    {
        var reference = await ReferenceGainAsync(40);
        var h = new Harness();
        await using var _ = h.Pipeline;

        h.Pipeline.SetVolume(40);
        await h.Pipeline.StartAsync(Format);

        Assert.InRange(h.CurrentPlayer.Volume, 0.001f, 0.999f);
        Assert.Equal(reference, h.CurrentPlayer.Volume);
    }

    [Fact]
    public async Task Volume_IsReappliedOnEveryStreamStart()
    {
        var h = new Harness();
        await using var _ = h.Pipeline;

        await h.Pipeline.StartAsync(Format);
        h.Pipeline.SetVolume(40);
        var runningGain = h.CurrentPlayer.Volume;
        Assert.InRange(runningGain, 0.001f, 0.999f);

        // Pause/stop followed by a new stream (or a track skip) starts a new player.
        await h.Pipeline.StopAsync();
        await h.Pipeline.StartAsync(Format);
        Assert.Equal(2, h.Players.Count);
        Assert.Equal(runningGain, h.CurrentPlayer.Volume);

        await h.Pipeline.StartAsync(Format); // restart without a stop in between
        Assert.Equal(3, h.Players.Count);
        Assert.Equal(runningGain, h.CurrentPlayer.Volume);
    }

    [Fact]
    public async Task LastVolumeSet_WinsOverEarlierOnes()
    {
        var h = new Harness();
        await using var _ = h.Pipeline;

        h.Pipeline.SetVolume(80);
        await h.Pipeline.StartAsync(Format);
        h.Pipeline.SetVolume(20);
        var lowGain = h.CurrentPlayer.Volume;

        await h.Pipeline.StopAsync();
        await h.Pipeline.StartAsync(Format);

        Assert.Equal(lowGain, h.CurrentPlayer.Volume);
    }

    /// <summary>The gain the pipeline gives a running player at <paramref name="volume"/> percent.</summary>
    private static async Task<float> ReferenceGainAsync(int volume)
    {
        var h = new Harness();
        await using var _ = h.Pipeline;
        await h.Pipeline.StartAsync(Format);
        h.Pipeline.SetVolume(volume);
        return h.CurrentPlayer.Volume;
    }

    private sealed class FakeDecoderFactory : IAudioDecoderFactory
    {
        public IAudioDecoder Create(AudioFormat format) => new FakeDecoder(format);
        public bool IsSupported(string codec) => true;
    }

    private sealed class FakeDecoder : IAudioDecoder
    {
        public FakeDecoder(AudioFormat format) => Format = format;
        public AudioFormat Format { get; }
        public int MaxSamplesPerFrame => 4096;
        public int Decode(ReadOnlySpan<byte> encodedData, Span<float> decodedSamples) => 0;
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class FakeSampleSource : IAudioSampleSource
    {
        public FakeSampleSource(AudioFormat format) => Format = format;
        public AudioFormat Format { get; }
        public int Read(float[] buffer, int offset, int count) => 0;
    }

    private sealed class FakePlayer : IAudioPlayer
    {
        public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;
        public float Volume { get; set; } = 1f;
        public bool IsMuted { get; set; }
        public int OutputLatencyMs => 0;

        public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
        {
            State = AudioPlayerState.Stopped;
            return Task.CompletedTask;
        }

        public void SetSampleSource(IAudioSampleSource source) { }
        public void Play() => State = AudioPlayerState.Playing;
        public void Pause() => State = AudioPlayerState.Paused;
        public void Stop() => State = AudioPlayerState.Stopped;
        public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public event EventHandler<AudioPlayerState>? StateChanged { add { } remove { } }
        public event EventHandler<AudioPlayerError>? ErrorOccurred { add { } remove { } }
    }

    /// <summary>
    /// Accepts the property writes <see cref="AudioPipeline.StartAsync"/> makes on its buffer and
    /// ignores everything else; <see cref="FakeTimedAudioBuffer"/> throws on those writes.
    /// </summary>
    private sealed class RecordingTimedAudioBuffer : ITimedAudioBuffer
    {
        public RecordingTimedAudioBuffer(AudioFormat format) => Format = format;

        public AudioFormat Format { get; }
        public long OutputLatencyMicroseconds { get; set; }
        public long CalibratedStartupLatencyMicroseconds { get; set; }
        public string TimingSourceName { get; set; } = "";
        public double TargetBufferMilliseconds { get; set; }

        public int Read(Span<float> destination, long currentTimeMicroseconds) => 0;
        public int ReadRaw(Span<float> destination, long currentTimeMicroseconds) => 0;
        public AudioBufferStats GetStats() => default!;
        public double SmoothedSyncErrorMicroseconds => 0;
        public void NotifyExternalCorrection(int droppedSamples, int insertedSamples) { }
        public SyncCorrectionOptions SyncOptions => default!;
        public double BufferedMilliseconds => 0;
        public bool IsReadyForPlayback => false;
        public long SyncErrorMicroseconds => 0;
        public double TargetPlaybackRate => 1.0;

        public event Action<double>? TargetPlaybackRateChanged { add { } remove { } }

        public void Write(ReadOnlySpan<float> samples, long timestampMicroseconds) { }
        public void ReportExternalPlaybackRate(double rate) { }
        public void NotifyReconnect() { }
        public void Clear() { }
        public void Dispose() { }
    }
}
