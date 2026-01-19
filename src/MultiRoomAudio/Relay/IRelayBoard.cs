using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Interface for relay board implementations.
/// </summary>
public interface IRelayBoard : IDisposable
{
    /// <summary>
    /// Whether the board is connected and ready.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Open connection to the first available device.
    /// </summary>
    bool Open();

    /// <summary>
    /// Open connection to a device by serial number.
    /// </summary>
    bool OpenBySerial(string serialNumber);

    /// <summary>
    /// Close the connection.
    /// </summary>
    void Close();

    /// <summary>
    /// Set a relay channel on or off.
    /// </summary>
    bool SetRelay(int channel, bool on);

    /// <summary>
    /// Get the current state of a relay channel.
    /// </summary>
    RelayState GetRelay(int channel);

    /// <summary>
    /// Turn all relays off.
    /// </summary>
    bool AllOff();

    /// <summary>
    /// Get the current state of all relays as a bitmask (bit 0 = relay 1, supports up to 16 relays).
    /// </summary>
    int CurrentState { get; }
}
