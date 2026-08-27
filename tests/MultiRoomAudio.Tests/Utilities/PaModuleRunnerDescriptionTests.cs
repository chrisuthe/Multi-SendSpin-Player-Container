using MultiRoomAudio.Utilities;
using Xunit;

namespace MultiRoomAudio.Tests.Utilities;

/// <summary>
/// Tests for the <c>sink_properties=device.description=...</c> argument built by
/// <see cref="PaModuleRunner"/>. Descriptions used to have their spaces replaced with
/// underscores to work around PulseAudio's proplist parser; they are now carried through
/// intact by quoting the value twice, so the escaping is what needs guarding.
/// </summary>
public class PaModuleRunnerDescriptionTests
{
    [Fact]
    public void BuildDescriptionArg_QuotesTwiceAndKeepsSpaces()
    {
        // Outer double quotes are stripped by pactl's module argument parser, inner single
        // quotes by pa_proplist_from_string. One level of either fails module init.
        Assert.Equal(
            "sink_properties=\"device.description='Creative X-Fi Front Left Front Right'\"",
            PaModuleRunner.BuildDescriptionArg("Creative X-Fi Front Left Front Right"));
    }

    [Theory]
    [InlineData("Bang & Olufsen", "Bang & Olufsen")]                 // no longer mangled to _and_
    [InlineData("Zone 1 | back", "Zone 1 | back")]
    [InlineData("Living Room  50% #2", "Living Room  50% #2")]
    [InlineData("  padded  ", "padded")]
    public void SanitizeDescription_LeavesHarmlessCharactersAlone(string input, string expected)
    {
        Assert.Equal(expected, PaModuleRunner.SanitizeDescription(input));
    }

    [Theory]
    [InlineData("say \"hi\"", "say hi")]
    [InlineData("it's mine", "its mine")]
    [InlineData("back\\slash", "backslash")]
    [InlineData("two\nlines", "two lines")]
    [InlineData("carriage\rreturn", "carriagereturn")]
    [InlineData("null\0char", "nullchar")]
    public void SanitizeDescription_StripsCharactersThatWouldBreakQuoting(string input, string expected)
    {
        Assert.Equal(expected, PaModuleRunner.SanitizeDescription(input));
    }

    [Fact]
    public void BuildDescriptionArg_CannotBeEscapedByQuotesInTheDescription()
    {
        // A description crafted to close the quoting and append another property must not
        // produce anything outside the single quoted value.
        var arg = PaModuleRunner.BuildDescriptionArg("evil' device.class='sound");

        Assert.Equal("sink_properties=\"device.description='evil device.class=sound'\"", arg);
        Assert.Equal(2, arg.Count(c => c == '\''));
        Assert.Equal(2, arg.Count(c => c == '"'));
    }
}
