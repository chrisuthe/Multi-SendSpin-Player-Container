using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Minimal <see cref="ITimedAudioBuffer"/> test double for exercising
/// <see cref="MultiRoomAudio.Audio.BufferedAudioSampleSource"/>'s read and overrun-detection paths.
/// <see cref="ReadRaw"/> returns a full buffer of silence; <see cref="GetStats"/> returns the
/// caller-supplied snapshot. Members not used by those paths throw.
/// </summary>
internal sealed class FakeTimedAudioBuffer : ITimedAudioBuffer
{
    private readonly AudioBufferStats _stats;

    public FakeTimedAudioBuffer(AudioFormat format, AudioBufferStats stats)
    {
        Format = format;
        _stats = stats;
    }

    public AudioFormat Format { get; }

    public int ReadRaw(Span<float> destination, long currentTimeMicroseconds)
    {
        destination.Clear();
        return destination.Length;
    }

    public AudioBufferStats GetStats() => _stats;

    public double SmoothedSyncErrorMicroseconds => 0;

    public void NotifyExternalCorrection(int droppedSamples, int insertedSamples)
    {
    }

    // --- Unused by the sample-source read/overrun path ---
    public SyncCorrectionOptions SyncOptions => throw new NotSupportedException();
    public double BufferedMilliseconds => throw new NotSupportedException();
    public double TargetBufferMilliseconds { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public bool IsReadyForPlayback => throw new NotSupportedException();
    public long OutputLatencyMicroseconds { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public long CalibratedStartupLatencyMicroseconds { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public string TimingSourceName { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public long SyncErrorMicroseconds => throw new NotSupportedException();
    public double TargetPlaybackRate => throw new NotSupportedException();

    public event Action<double>? TargetPlaybackRateChanged
    {
        add { }
        remove { }
    }

    public void Write(ReadOnlySpan<float> samples, long timestampMicroseconds) => throw new NotSupportedException();
    public int Read(Span<float> destination, long currentTimeMicroseconds) => throw new NotSupportedException();
    public void ReportExternalPlaybackRate(double rate) => throw new NotSupportedException();
    public void NotifyReconnect() => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Dispose() { }
}
