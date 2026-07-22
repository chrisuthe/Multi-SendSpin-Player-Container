using MultiRoomAudio.Audio;
using Xunit;

namespace MultiRoomAudio.Tests;

public class VolumeCurveTests
{
    [Fact]
    public void Zero_IsSilent() => Assert.Equal(0f, VolumeCurve.ToGain(0));

    [Fact]
    public void OneHundred_IsUnityGain() => Assert.Equal(1f, VolumeCurve.ToGain(100));

    [Theory]
    [InlineData(-10)]
    [InlineData(150)]
    public void OutOfRangeInputs_AreClamped(int percent)
    {
        var gain = VolumeCurve.ToGain(percent);

        Assert.InRange(gain, 0f, 1f);
    }

    [Theory]
    [InlineData(50, 0.125)]
    [InlineData(25, 0.015625)]
    [InlineData(10, 0.001)]
    public void FollowsACubicTaper(int percent, double expected)
    {
        // Matches PulseAudio's pa_sw_volume_to_linear, so software volume and the hardware
        // volume applied via pactl behave the same way.
        Assert.Equal(expected, VolumeCurve.ToGain(percent), precision: 6);
    }

    [Fact]
    public void IsMonotonic()
    {
        for (var percent = 1; percent <= 100; percent++)
            Assert.True(VolumeCurve.ToGain(percent) > VolumeCurve.ToGain(percent - 1),
                $"gain at {percent} should exceed gain at {percent - 1}");
    }

    [Fact]
    public void LowSettingsAreQuieterThanTheOldLinearTaper()
    {
        // The #263 report: level 2 was "already quite loud" because the old taper applied
        // 0.02 amplitude (-34 dB) there rather than something proportionate.
        Assert.True(VolumeCurve.ToGain(2) < 2 / 100.0f);
        Assert.True(VolumeCurve.ToGain(20) < 20 / 100.0f);
    }

    [Fact]
    public void SpreadsMoreRangeAcrossTheUpperHalfOfTheSlider()
    {
        // A linear taper puts only 6 dB between 50 and 100, which is why increments felt
        // wrong. Cubic gives the top half a far wider span.
        var linearSpan = 1.0f - 0.5f;
        var cubicSpan = VolumeCurve.ToGain(100) - VolumeCurve.ToGain(50);

        Assert.True(cubicSpan > linearSpan);
    }
}
