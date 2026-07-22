using Microsoft.Extensions.Logging;
using MultiRoomAudio.Audio;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Xunit;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Guards <see cref="BufferedAudioSampleSource"/>'s overrun detection (issue #233): a one-time
/// startup sample discard (SDK re-anchors playback, dropping samples whose scheduled play-time
/// already passed) must NOT be reported as a buffer-full overrun. Only a genuine overrun — where
/// the SDK's <c>OverrunCount</c> advances — should raise the loud ERROR.
/// </summary>
public class OverrunDetectionTests
{
    private static AudioFormat Stereo48k => new()
    {
        Codec = "pcm",
        SampleRate = 48000,
        Channels = 2,
        BitDepth = 32
    };

    /// <summary>
    /// The reporter's residual case: 9216 samples dropped, but <c>OverrunCount == 0</c> and the
    /// buffer is at 838ms of a 30s capacity (not full). This is a benign startup discard and must
    /// not produce an ERROR claiming "buffer is full and Read() isn't consuming".
    /// </summary>
    [Fact]
    public void StartupDiscard_WithoutOverrunIncrement_DoesNotLogError()
    {
        var stats = new AudioBufferStats
        {
            DroppedSamples = 9216,
            OverrunCount = 0,
            BufferedMs = 838,
            TargetMs = 250,
            IsPlaybackActive = true
        };
        var (source, logger) = CreateSource(stats);

        ReadOnce(source);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("discard", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Regression guard: a real overrun (the SDK's <c>OverrunCount</c> advances while the buffer is
    /// near capacity) must still be reported loudly at ERROR.
    /// </summary>
    [Fact]
    public void GenuineOverrun_WithOverrunIncrement_LogsError()
    {
        var stats = new AudioBufferStats
        {
            DroppedSamples = 9216,
            OverrunCount = 1,
            BufferedMs = 29988,
            TargetMs = 250,
            IsPlaybackActive = true
        };
        var (source, logger) = CreateSource(stats);

        ReadOnce(source);

        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("BUFFER OVERFLOW DETECTED"));
    }

    private static (BufferedAudioSampleSource Source, CapturingLogger<BufferedAudioSampleSource> Logger) CreateSource(
        AudioBufferStats stats)
    {
        var buffer = new FakeTimedAudioBuffer(Stereo48k, stats);
        var logger = new CapturingLogger<BufferedAudioSampleSource>();
        var source = new BufferedAudioSampleSource(buffer, () => 1_000_000, logger);
        return (source, logger);
    }

    private static void ReadOnce(BufferedAudioSampleSource source)
    {
        var output = new float[480 * 2]; // one 10ms stereo callback
        source.Read(output, 0, output.Length);
    }
}
