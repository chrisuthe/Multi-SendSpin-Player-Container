using MultiRoomAudio.Models;
using MultiRoomAudio.Services;
using Xunit;

namespace MultiRoomAudio.Tests.Services;

/// <summary>
/// Tests for <see cref="CustomSinksService.OrderByDependencies"/>, the topological sort that
/// decides the order custom sinks are loaded on startup. Regression coverage for #247, where a
/// combine sink whose slaves are remap sinks failed to load because the remaps were loaded after it.
/// </summary>
public class CustomSinkOrderingTests
{
    private static CustomSinkConfiguration Combine(string name, params string[] slaves) => new()
    {
        Name = name,
        Type = CustomSinkType.Combine,
        Slaves = slaves.ToList(),
    };

    private static CustomSinkConfiguration Remap(string name, string master) => new()
    {
        Name = name,
        Type = CustomSinkType.Remap,
        MasterSink = master,
    };

    private static int IndexOf(IReadOnlyList<CustomSinkConfiguration> ordered, string name) =>
        ordered.ToList().FindIndex(c => c.Name == name);

    [Fact]
    public void CombineOfRemaps_LoadsRemapsBeforeCombine()
    {
        // The #247 topology: a combine sink "Master" built from two remap sinks.
        var configs = new List<CustomSinkConfiguration>
        {
            Combine("Master", "Center", "FrontLR"),
            Remap("Center", "alsa_output.usb-card"),
            Remap("FrontLR", "alsa_output.usb-card"),
        };

        var ordered = CustomSinksService.OrderByDependencies(configs, out var cycles);

        Assert.Empty(cycles);
        Assert.True(IndexOf(ordered, "Center") < IndexOf(ordered, "Master"));
        Assert.True(IndexOf(ordered, "FrontLR") < IndexOf(ordered, "Master"));
    }

    [Fact]
    public void RemapOfCombine_LoadsCombineBeforeRemap()
    {
        // The reverse topology the old type-based sort assumed: a remap using a combine as master.
        var configs = new List<CustomSinkConfiguration>
        {
            Remap("Zone1", "Combined"),
            Combine("Combined", "alsa_output.a", "alsa_output.b"),
        };

        var ordered = CustomSinksService.OrderByDependencies(configs, out var cycles);

        Assert.Empty(cycles);
        Assert.True(IndexOf(ordered, "Combined") < IndexOf(ordered, "Zone1"));
    }

    [Fact]
    public void HardwareReferences_AreIgnoredAndAllSinksPreserved()
    {
        var configs = new List<CustomSinkConfiguration>
        {
            Combine("Master", "alsa_output.hw1", "alsa_output.hw2"),
            Remap("Solo", "alsa_output.hw3"),
        };

        var ordered = CustomSinksService.OrderByDependencies(configs, out var cycles);

        Assert.Empty(cycles);
        Assert.Equal(
            configs.Select(c => c.Name).OrderBy(n => n),
            ordered.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public void Cycle_DoesNotHangAndReportsCycle()
    {
        // Not a valid real-world config, but the sort must terminate and surface the cycle
        // rather than recurse forever.
        var configs = new List<CustomSinkConfiguration>
        {
            Remap("A", "B"),
            Remap("B", "A"),
        };

        var ordered = CustomSinksService.OrderByDependencies(configs, out var cycles);

        Assert.NotEmpty(cycles);
        Assert.Equal(2, ordered.Count);
    }
}
