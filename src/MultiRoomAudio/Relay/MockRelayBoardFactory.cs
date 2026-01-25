using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Factory for creating mock relay board instances.
/// Used when MOCK_HARDWARE mode is enabled for testing without real hardware.
/// </summary>
public class MockRelayBoardFactory : IRelayBoardFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MockRelayBoardFactory> _logger;

    public MockRelayBoardFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MockRelayBoardFactory>();
    }

    /// <inheritdoc />
    public IRelayBoard CreateBoard(string boardId, RelayBoardType boardType)
    {
        // Extract serial from board ID for HID boards (format: "HID:SERIAL")
        var serial = boardId.StartsWith("HID:") ? boardId.Substring(4) : boardId;

        // Determine channel count based on board type and known mock devices
        var channelCount = GetMockChannelCount(boardId, boardType);

        _logger.LogDebug("Creating mock relay board: BoardId={BoardId}, BoardType={BoardType}, Serial={Serial}, Channels={Channels}",
            boardId, boardType, serial, channelCount);

        return new MockRelayBoard(
            logger: _loggerFactory.CreateLogger<MockRelayBoard>(),
            serialNumber: serial,
            boardType: boardType == RelayBoardType.Unknown ? RelayBoardType.Mock : boardType,
            channelCount: channelCount
        );
    }

    /// <inheritdoc />
    public bool CanCreate(string boardId, RelayBoardType boardType)
    {
        // Mock factory can create any board type
        return true;
    }

    /// <summary>
    /// Get the channel count for a mock board based on its ID.
    /// </summary>
    private static int GetMockChannelCount(string boardId, RelayBoardType boardType)
    {
        // Match known mock devices from MockRelayDeviceEnumerator
        return boardId switch
        {
            "MOCK001" or "MOCK002" => 8, // FTDI mock boards
            "HID:QAAMZ" => 4, // 4-channel HID mock board
            "HID:ABCDE" or "HID:MOCK8" => 8, // 8-channel HID mock board
            _ when boardId.StartsWith("MODBUS:", StringComparison.OrdinalIgnoreCase) => 16, // Default 16 for Modbus
            _ => boardType switch
            {
                RelayBoardType.UsbHid => 4,
                RelayBoardType.Modbus => 16,
                _ => 8 // FTDI and others default to 8
            }
        };
    }
}
