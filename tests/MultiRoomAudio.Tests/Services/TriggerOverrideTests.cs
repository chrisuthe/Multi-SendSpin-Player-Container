using MultiRoomAudio.Models;
using Xunit;

namespace MultiRoomAudio.Tests.Services;

public class TriggerModelsTests
{
    [Fact]
    public void TriggerResponse_IsOverridden_DefaultsFalse()
    {
        var r = new TriggerResponse(
            Channel: 1, CustomSinkName: null, CustomSinkDisplayName: null,
            OffDelaySeconds: 60, ZoneName: null, RelayState: RelayState.Off,
            IsActive: false, LastActivated: null, ScheduledOffTime: null);
        Assert.False(r.IsOverridden);
    }

    [Fact]
    public void RelayBoardType_HasVirtual()
        => Assert.True(System.Enum.IsDefined(typeof(RelayBoardType), RelayBoardType.Virtual));
}
