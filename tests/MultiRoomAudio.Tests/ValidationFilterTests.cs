using MultiRoomAudio.Models;
using MultiRoomAudio.Utilities;
using Xunit;

namespace MultiRoomAudio.Tests;

/// <summary>
/// Covers the filter that makes DataAnnotations on minimal-API request records actually bite.
/// Minimal APIs ignore them by default, so without this an out-of-range offset would reach the
/// handler and be clamped, reporting success for a value the caller never got.
/// </summary>
public class ValidationFilterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(5000)]
    public void Validate_AcceptsDelaysInSpecRange(int delayMs)
    {
        Assert.Null(ValidationFilter<OffsetRequest>.Validate(new OffsetRequest(delayMs)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-500)]
    [InlineData(5001)]
    [InlineData(int.MaxValue)]
    public void Validate_RejectsDelaysOutsideSpecRange(int delayMs)
    {
        Assert.NotNull(ValidationFilter<OffsetRequest>.Validate(new OffsetRequest(delayMs)));
    }

    /// <summary>
    /// The rejection has to say what was wrong, so a caller sending -500 learns the range rather
    /// than getting a bare 400.
    /// </summary>
    [Fact]
    public void Validate_ExplainsTheRange()
    {
        var message = ValidationFilter<OffsetRequest>.Validate(new OffsetRequest(-500));

        Assert.NotNull(message);
        Assert.Contains("0", message);
        Assert.Contains("5000", message);
    }

    [Fact]
    public void Validate_StillEnforcesOtherRequestTypes()
    {
        Assert.Null(ValidationFilter<VolumeRequest>.Validate(new VolumeRequest(50)));
        Assert.NotNull(ValidationFilter<VolumeRequest>.Validate(new VolumeRequest(101)));
    }
}
