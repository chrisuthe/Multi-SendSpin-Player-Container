using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Factory for creating relay board instances.
/// Implementations handle the creation of appropriate board types (FTDI, HID, or Mock).
/// </summary>
public interface IRelayBoardFactory
{
    /// <summary>
    /// Create a relay board instance for the given board ID and type.
    /// The board is NOT connected - call Open() or OpenBySerial() after creation.
    /// </summary>
    /// <param name="boardId">Board identifier (serial number or HID:serial format).</param>
    /// <param name="boardType">Type of relay board to create.</param>
    /// <returns>A new relay board instance (not yet connected).</returns>
    IRelayBoard CreateBoard(string boardId, RelayBoardType boardType);

    /// <summary>
    /// Check if this factory can create a board of the given type.
    /// For real hardware, this checks if the required library is available.
    /// For mock mode, this always returns true.
    /// </summary>
    /// <param name="boardId">Board identifier.</param>
    /// <param name="boardType">Type of relay board.</param>
    /// <returns>True if the factory can create this board type.</returns>
    bool CanCreate(string boardId, RelayBoardType boardType);
}
