using MultiRoomAudio.Relay;
using Xunit;

namespace MultiRoomAudio.Tests.Relay;

/// <summary>
/// Wire-protocol tests for the relay boards.
///
/// Expected values are taken from the protocol documented in CLAUDE.md, not read back
/// out of the implementation, so these fail if either side drifts from the spec.
/// These encodings cannot be verified without hardware, and getting one wrong silently
/// switches the wrong relay - which is why they are pinned here.
/// </summary>
public class RelayProtocolTests
{
    // ---------------------------------------------------------------------
    // LCUS: [0xA0][Channel][Operation][Checksum], checksum = (0xA0+ch+op) & 0xFF
    // ---------------------------------------------------------------------

    [Theory]
    // Exact frames documented in CLAUDE.md.
    [InlineData(1, true, new byte[] { 0xA0, 0x01, 0x01, 0xA2 })]
    [InlineData(1, false, new byte[] { 0xA0, 0x01, 0x00, 0xA1 })]
    [InlineData(8, true, new byte[] { 0xA0, 0x08, 0x01, 0xA9 })]
    public void Lcus_BuildCommand_MatchesDocumentedFrames(int channel, bool on, byte[] expected)
    {
        Assert.Equal(expected, LcusRelayBoard.BuildCommand(channel, on));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Lcus_BuildCommand_ChecksumIsPrefixPlusChannelPlusOperation(int channel)
    {
        foreach (var on in new[] { true, false })
        {
            var frame = LcusRelayBoard.BuildCommand(channel, on);

            Assert.Equal(4, frame.Length);
            Assert.Equal(0xA0, frame[0]);
            Assert.Equal((byte)channel, frame[1]);
            Assert.Equal(on ? 0x01 : 0x00, frame[2]);
            Assert.Equal((byte)((0xA0 + channel + (on ? 1 : 0)) & 0xFF), frame[3]);
        }
    }

    // ---------------------------------------------------------------------
    // Modbus ASCII: ":" + address + function + data + LRC + CRLF
    // ---------------------------------------------------------------------

    [Fact]
    public void Modbus_BuildCommand_MatchesDocumentedProbeCommand()
    {
        // CLAUDE.md documents the CH340 read-coils probe as :FE0100000010F1\r\n
        var command = ModbusRelayBoard.BuildModbusCommand(0xFE, 0x01, 0x00, 0x00, 0x00, 0x10);

        Assert.Equal(":FE0100000010F1\r\n", command);
    }

    [Theory]
    // Write single coil (0x05) to device 0xFE. Coil address is 0-based, ON = 0xFF00.
    [InlineData(0x00, 0xFF, ":FE050000FF00FE\r\n")]  // channel 1 ON
    [InlineData(0x00, 0x00, ":FE0500000000FD\r\n")]  // channel 1 OFF
    [InlineData(0x0F, 0xFF, ":FE05000FFF00EF\r\n")]  // channel 16 ON
    public void Modbus_BuildCommand_MatchesDocumentedWriteCoilFrames(
        byte coilAddressLo, byte valueHi, string expected)
    {
        var command = ModbusRelayBoard.BuildModbusCommand(
            0xFE, 0x05, 0x00, coilAddressLo, valueHi, 0x00);

        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData(0x05, 0x00, 0x00, 0xFF, 0x00)]
    [InlineData(0x05, 0x00, 0x07, 0x00, 0x00)]
    [InlineData(0x0F, 0x00, 0x10, 0x02, 0xFF)]
    public void Modbus_Lrc_MakesFrameBytesSumToZeroModulo256(
        byte function, byte d0, byte d1, byte d2, byte d3)
    {
        var command = ModbusRelayBoard.BuildModbusCommand(0xFE, function, d0, d1, d2, d3);

        Assert.StartsWith(":", command);
        Assert.EndsWith("\r\n", command);

        // An LRC is correct exactly when every byte in the frame, itself included,
        // sums to zero modulo 256.
        var body = command[1..^2];
        var sum = 0;
        for (var i = 0; i < body.Length; i += 2)
        {
            sum += Convert.ToByte(body.Substring(i, 2), 16);
        }

        Assert.Equal(0, sum & 0xFF);
    }

    // ---------------------------------------------------------------------
    // FTDI bit masks. The 4-channel Denkovi board wires relays to the ODD pins
    // (D1, D3, D5, D7) rather than D0-D3 - the quirk this suite exists to pin.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0b0000_0001)]
    [InlineData(2, 0b0000_0010)]
    [InlineData(4, 0b0000_1000)]
    [InlineData(8, 0b1000_0000)]
    public void Ftdi_EightChannelBoard_UsesSequentialPins(int channel, int expectedMask)
    {
        Assert.Equal((ushort)expectedMask, FtdiRelayBoard.GetBitMaskForChannel(channel, channelCount: 8));
    }

    [Theory]
    // CLAUDE.md: Denkovi DAE-CB/Ro4-USB maps relay 1-4 to bits 1, 3, 5, 7.
    [InlineData(1, 0b0000_0010)]
    [InlineData(2, 0b0000_1000)]
    [InlineData(3, 0b0010_0000)]
    [InlineData(4, 0b1000_0000)]
    public void Ftdi_FourChannelBoard_UsesOddPins(int channel, int expectedMask)
    {
        Assert.Equal((ushort)expectedMask, FtdiRelayBoard.GetBitMaskForChannel(channel, channelCount: 4));
    }

    [Fact]
    public void Ftdi_FourChannelMasks_AreDistinctAndNotSequential()
    {
        var masks = new[] { 1, 2, 3, 4 }
            .Select(c => FtdiRelayBoard.GetBitMaskForChannel(c, channelCount: 4))
            .ToArray();

        Assert.Equal(4, masks.Distinct().Count());

        // Guards against a "simplification" back to sequential pins, which would
        // energise the wrong relay on a 4-channel board.
        var sequential = new[] { 1, 2, 3, 4 }
            .Select(c => FtdiRelayBoard.GetBitMaskForChannel(c, channelCount: 8))
            .ToArray();

        Assert.NotEqual(sequential, masks);
    }
}
