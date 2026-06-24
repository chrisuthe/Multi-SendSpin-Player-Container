using MultiRoomAudio.Audio.PulseAudio;
using Xunit;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Guards the critical safety property of the audio-clock gate: it must default OFF so the SDK
/// stays on its VM-resilient MonotonicTimer. A regression to default-on would silently reintroduce
/// the output-prefill offset that pushes a player off the shared multi-room schedule.
/// </summary>
public class UseAudioClockGateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("anything-else")]
    public void DefaultsOff_WhenNotExplicitlyEnabled(string? value)
    {
        Assert.False(PulseAudioPlayer.ParseUseAudioClock(value));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("YES")]
    public void OptsIn_OnTruthyValues(string value)
    {
        Assert.True(PulseAudioPlayer.ParseUseAudioClock(value));
    }
}
