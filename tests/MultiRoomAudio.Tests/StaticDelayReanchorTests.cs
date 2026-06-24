using MultiRoomAudio.Services;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Xunit;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Guards the assumption behind <c>SetDelayOffset</c>'s use of <c>Pipeline.ReanchorTiming()</c>:
/// re-anchoring (which forwards to <see cref="TimedAudioBuffer.ResetSyncTracking"/>) must PRESERVE
/// the buffered audio, so a delay change applies in place. The previous approach (a full restart /
/// Clear) dumped the buffer and, with a server transmitting far ahead of playback, stalled for the
/// whole transmit-ahead window (tens of seconds of silence) on every nudge.
/// </summary>
public class StaticDelayReanchorTests
{
    private const int Rate = 48000;
    private const int Ch = 2;
    private const int ChunkFrames = 480; // 10ms
    private const int ChunkSamples = ChunkFrames * Ch;
    private const double UsPerFrame = 1_000_000.0 / Rate;
    private const long Start = 1_000_000_000L;

    /// <summary>
    /// Builds a buffer pre-filled with ~2s of audio (a deep buffer, as a far-ahead server produces)
    /// and reads a few chunks so playback is established.
    /// </summary>
    private static TimedAudioBuffer SetupPlaying()
    {
        var format = new AudioFormat { Codec = "pcm", SampleRate = Rate, Channels = Ch, BitDepth = 32 };
        var clock = new FakeClock(Start); // ServerToClientTime(0) == Start
        var buffer = new TimedAudioBuffer(
            format, clock, bufferCapacityMs: 4000, syncOptions: PlayerManagerService.PulseAudioSyncOptions);

        var data = new float[ChunkSamples];
        Array.Fill(data, 0.25f);

        long frames = 0;
        for (var k = 0; k < 200; k++) // 200 × 10ms = 2000ms buffered
        {
            buffer.Write(data, (long)(frames * UsPerFrame));
            frames += ChunkFrames;
        }

        var outBuf = new float[ChunkSamples];
        for (var i = 0; i < 10; i++) // establish playback, consume ~100ms
        {
            buffer.Read(outBuf, Start + (long)((long)i * ChunkFrames * UsPerFrame));
        }

        return buffer;
    }

    [Fact]
    public void Reanchor_PreservesBufferedAudio_NoSilenceGap()
    {
        var buffer = SetupPlaying();
        Assert.True(buffer.BufferedMilliseconds > 1000, "precondition: a deep buffer is present");

        buffer.ResetSyncTracking(); // what Pipeline.ReanchorTiming() forwards to

        Assert.True(
            buffer.BufferedMilliseconds > 1000,
            $"re-anchor must preserve buffered audio, but only {buffer.BufferedMilliseconds:F0}ms remained");
    }

    [Fact]
    public void Clear_EmptiesBuffer_TheFailureModeWeAvoid()
    {
        var buffer = SetupPlaying();
        Assert.True(buffer.BufferedMilliseconds > 1000);

        buffer.Clear();

        Assert.True(
            buffer.BufferedMilliseconds < 1,
            $"Clear should empty the buffer, but {buffer.BufferedMilliseconds:F0}ms remained");
    }
}
