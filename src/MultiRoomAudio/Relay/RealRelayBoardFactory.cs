using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Factory for creating real relay board instances (FTDI, HID, Modbus, and LCUS).
/// </summary>
public class RealRelayBoardFactory : IRelayBoardFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RealRelayBoardFactory> _logger;

    public RealRelayBoardFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RealRelayBoardFactory>();
    }

    /// <inheritdoc />
    public IRelayBoard CreateBoard(string boardId, RelayBoardType boardType)
    {
        _logger.LogDebug("Creating {BoardType} relay board for '{BoardId}'", boardType, boardId);

        return boardType switch
        {
            RelayBoardType.UsbHid => new HidRelayBoard(_loggerFactory.CreateLogger<HidRelayBoard>()),
            RelayBoardType.Ftdi => new FtdiRelayBoard(_loggerFactory.CreateLogger<FtdiRelayBoard>()),
            RelayBoardType.Modbus => CreateModbusBoard(boardId),
            RelayBoardType.Lcus => CreateLcusBoard(boardId),
            _ => throw new ArgumentException($"Unsupported board type: {boardType}", nameof(boardType))
        };
    }

    /// <summary>
    /// Create a Modbus relay board, extracting channel count from board ID if specified.
    /// </summary>
    private IRelayBoard CreateModbusBoard(string boardId)
    {
        // Default to 16 channels for Modbus boards (most common is Sainsmart 16-channel)
        // User can override in board settings after creation
        int channelCount = 16;

        return new ModbusRelayBoard(_loggerFactory.CreateLogger<ModbusRelayBoard>(), channelCount);
    }

    /// <summary>
    /// Create an LCUS relay board, extracting channel count from board ID if specified.
    /// </summary>
    private IRelayBoard CreateLcusBoard(string boardId)
    {
        // Default to 8 channels for LCUS boards (max supported by protocol)
        // Channel count is auto-detected when connecting via status query
        int channelCount = 8;

        return new LcusRelayBoard(_loggerFactory.CreateLogger<LcusRelayBoard>(), channelCount);
    }

    /// <inheritdoc />
    public bool CanCreate(string boardId, RelayBoardType boardType)
    {
        return boardType switch
        {
            RelayBoardType.UsbHid => true, // HID is always available via HidSharp
            RelayBoardType.Ftdi => FtdiRelayBoard.IsLibraryAvailable(),
            RelayBoardType.Modbus => true, // Serial ports are always available via System.IO.Ports
            RelayBoardType.Lcus => true, // Serial ports are always available via System.IO.Ports
            RelayBoardType.Mock => false, // Real factory doesn't create mock boards
            _ => false
        };
    }
}
