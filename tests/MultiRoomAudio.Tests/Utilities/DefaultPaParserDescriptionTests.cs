using MultiRoomAudio.Utilities;
using Xunit;

namespace MultiRoomAudio.Tests.Utilities;

/// <summary>
/// Tests for the reading half of the <c>sink_properties=device.description=...</c> contract:
/// what <see cref="DefaultPaParser"/> pulls back out of a default.pa line.
/// <see cref="PaModuleRunnerDescriptionTests"/> pins the writing half.
/// </summary>
public class DefaultPaParserDescriptionTests
{
    /// <summary>
    /// The quoting matrix. Only the two doubly-quoted forms actually load in PulseAudio, so
    /// those are the ones that have to come back whole; the rest are pinned to keep the parser
    /// a mirror of what PulseAudio itself accepts.
    /// </summary>
    [Theory]
    // Outer double / inner single - what PaModuleRunner.BuildDescriptionArg emits.
    [InlineData("sink_properties=\"device.description='Bedroom Rear'\"", "Bedroom Rear")]
    // Outer single / inner double - the other form PulseAudio loads.
    [InlineData("sink_properties='device.description=\"Bedroom Rear\"'", "Bedroom Rear")]
    // One level of quoting: PulseAudio rejects the line, so parsing stops at the space.
    [InlineData("sink_properties=device.description=\"Bedroom Rear\"", "Bedroom")]
    // No spaces, no quoting needed.
    [InlineData("sink_properties=device.description=BedroomRear", "BedroomRear")]
    public void ParseDescriptionFromArguments_HandlesEveryQuotingForm(string arguments, string expected)
    {
        Assert.Equal(expected, DefaultPaParser.ParseDescriptionFromArguments(
            "sink_name=mra_bedroom master=alsa_output.usb " + arguments));
    }

    [Theory]
    [InlineData("Bedroom Rear")]
    [InlineData("Bang & Olufsen | 50%")]
    [InlineData("Creative X-Fi Front Left Front Right")]
    public void ParseDescriptionFromArguments_RoundTripsWhatPaModuleRunnerWrites(string description)
    {
        var arguments = "sink_name=mra_zone " + PaModuleRunner.BuildDescriptionArg(description);

        Assert.Equal(description, DefaultPaParser.ParseDescriptionFromArguments(arguments));
    }

    [Fact]
    public void ParseDescriptionFromArguments_ReturnsNullWhenThereIsNoDescription()
    {
        Assert.Null(DefaultPaParser.ParseDescriptionFromArguments("sink_name=mra_zone master=alsa_output.usb"));
        Assert.Null(DefaultPaParser.ParseDescriptionFromArguments("sink_name=mra_zone sink_properties=device.class=sound"));
    }

    [Fact]
    public void ParseKeyValues_ReadsRemapArgumentsAlongsideAQuotedDescription()
    {
        var keyValues = DefaultPaParser.ParseKeyValues(
            "sink_name=mra_bedroom master=alsa_output.usb-0d8c_USB_Sound-00.analog-stereo " +
            "channels=2 channel_map=front-left,front-right master_channel_map=front-left,front-right " +
            "remix=no sink_properties=\"device.description='Bedroom Rear'\"");

        Assert.Equal("mra_bedroom", keyValues["sink_name"]);
        Assert.Equal("alsa_output.usb-0d8c_USB_Sound-00.analog-stereo", keyValues["master"]);
        Assert.Equal("2", keyValues["channels"]);
        Assert.Equal("front-left,front-right", keyValues["channel_map"]);
        Assert.Equal("front-left,front-right", keyValues["master_channel_map"]);
        Assert.Equal("no", keyValues["remix"]);
        Assert.Equal("device.description='Bedroom Rear'", keyValues["sink_properties"]);
    }

    [Fact]
    public void ParseKeyValues_ReadsCombineArguments()
    {
        var keyValues = DefaultPaParser.ParseKeyValues(
            "sink_name=mra_whole_house slaves=sink_a,sink_b,sink_c " +
            "sink_properties='device.description=\"Whole House\"'");

        Assert.Equal("mra_whole_house", keyValues["sink_name"]);
        Assert.Equal("sink_a,sink_b,sink_c", keyValues["slaves"]);
        Assert.Equal("device.description=\"Whole House\"", keyValues["sink_properties"]);
    }
}
