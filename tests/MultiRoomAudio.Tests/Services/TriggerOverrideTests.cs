using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MultiRoomAudio.Models;
using MultiRoomAudio.Relay;
using MultiRoomAudio.Services;
using Xunit;

namespace MultiRoomAudio.Tests.Services;

public class TriggerOverrideBehaviorTests
{
    // Builds a TriggerService in mock mode with one virtual board + one configured channel.
    private static async Task<(TriggerService svc, string boardId)> SetupAsync()
    {
        var svc = TriggerTestHarness.CreateMockService();
        svc.SetEnabled(true);
        var boardId = "VIRTUAL:test01";
        svc.AddBoard(boardId, "Test Zone", channelCount: 2, boardType: RelayBoardType.Virtual);
        svc.ConfigureTrigger(boardId, channel: 1, customSinkNames: new List<string> { "sink1" }, offDelaySeconds: 30, zoneName: "Zone 1");
        await Task.CompletedTask;
        return (svc, boardId);
    }

    [Fact]
    public async Task Override_On_ForcesRelayOn_AndReportsOverridden()
    {
        var (svc, boardId) = await SetupAsync();
        Assert.True(svc.SetOverride(boardId, 1, true));

        var ch = svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == 1);
        Assert.Equal(RelayState.On, ch.RelayState);
        Assert.True(ch.IsOverridden);
    }

    [Fact]
    public async Task Override_Suspends_AutoOff_WhilePlaybackStops()
    {
        var (svc, boardId) = await SetupAsync();
        svc.OnPlayerStarted("p1", "sink1");     // auto-on
        svc.SetOverride(boardId, 1, true);      // grab manual control
        svc.OnPlayerStopped("p1", "sink1");     // would normally schedule off

        var ch = svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == 1);
        Assert.Equal(RelayState.On, ch.RelayState);   // still on — auto-off suppressed
        Assert.True(ch.IsOverridden);
        Assert.Null(ch.ScheduledOffTime);             // no off-timer scheduled
    }

    [Fact]
    public async Task Release_WhileIdle_SchedulesOff()
    {
        var (svc, boardId) = await SetupAsync();
        svc.SetOverride(boardId, 1, true);   // on, no players
        svc.SetOverride(boardId, 1, false);  // release while idle

        var ch = svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == 1);
        Assert.False(ch.IsOverridden);
        Assert.NotNull(ch.ScheduledOffTime);  // off-delay started
    }

    [Fact]
    public async Task Release_WhilePlaying_StaysOn_NoOffTimer()
    {
        var (svc, boardId) = await SetupAsync();
        svc.OnPlayerStarted("p1", "sink1");
        svc.SetOverride(boardId, 1, true);
        svc.SetOverride(boardId, 1, false);  // release with a player still active

        var ch = svc.GetBoardStatus(boardId)!.Triggers.Single(t => t.Channel == 1);
        Assert.Equal(RelayState.On, ch.RelayState);
        Assert.Null(ch.ScheduledOffTime);
    }

    [Fact]
    public async Task TriggersChanged_FiresOnOverride()
    {
        var (svc, boardId) = await SetupAsync();
        int fires = 0;
        svc.TriggersChanged += () => fires++;
        svc.SetOverride(boardId, 1, true);
        Assert.True(fires >= 1);
    }
}

public class TriggerModelsTests
{
    [Fact]
    public void TriggerResponse_IsOverridden_DefaultsFalse()
    {
        var r = new TriggerResponse(
            Channel: 1,
            CustomSinkNames: new List<string>(),
            CustomSinkDisplayNames: new List<string>(),
            OffDelaySeconds: 60, ZoneName: null, RelayState: RelayState.Off,
            IsActive: false, LastActivated: null, ScheduledOffTime: null);
        Assert.False(r.IsOverridden);
    }

    [Fact]
    public void RelayBoardType_HasVirtual()
        => Assert.True(System.Enum.IsDefined(typeof(RelayBoardType), RelayBoardType.Virtual));
}
