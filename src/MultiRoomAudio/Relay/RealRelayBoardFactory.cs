using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Factory for creating real relay board instances (FTDI and HID).
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
            _ => throw new ArgumentException($"Unsupported board type: {boardType}", nameof(boardType))
        };
    }

    /// <inheritdoc />
    public bool CanCreate(string boardId, RelayBoardType boardType)
    {
        return boardType switch
        {
            RelayBoardType.UsbHid => true, // HID is always available via HidSharp
            RelayBoardType.Ftdi => FtdiRelayBoard.IsLibraryAvailable(),
            RelayBoardType.Mock => false, // Real factory doesn't create mock boards
            _ => false
        };
    }
}
