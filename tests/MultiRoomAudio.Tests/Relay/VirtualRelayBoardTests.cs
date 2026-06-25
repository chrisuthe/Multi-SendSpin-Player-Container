using MultiRoomAudio.Models;
using MultiRoomAudio.Relay;
using Xunit;

namespace MultiRoomAudio.Tests.Relay;

public class VirtualRelayBoardTests
{
    [Fact]
    public void SetAndGet_RoundTrips()
    {
        var b = new VirtualRelayBoard(serialNumber: "VIRTUAL:x", channelCount: 4);
        Assert.True(b.Open());
        Assert.True(b.IsConnected);
        Assert.True(b.SetRelay(2, true));
        Assert.Equal(RelayState.On, b.GetRelay(2));
        Assert.Equal(RelayState.Off, b.GetRelay(1));
        Assert.Equal(0b10, b.CurrentState);
    }

    [Fact]
    public void AllOff_ClearsState()
    {
        var b = new VirtualRelayBoard(serialNumber: "VIRTUAL:x", channelCount: 4);
        b.Open();
        b.SetRelay(1, true);
        Assert.True(b.AllOff());
        Assert.Equal(0, b.CurrentState);
    }

    [Fact]
    public void RealFactory_CreatesVirtualBoard()
    {
        var f = new RealRelayBoardFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        Assert.True(f.CanCreate("VIRTUAL:x", RelayBoardType.Virtual));
        var board = f.CreateBoard("VIRTUAL:x", RelayBoardType.Virtual);
        Assert.IsType<VirtualRelayBoard>(board);
    }
}
