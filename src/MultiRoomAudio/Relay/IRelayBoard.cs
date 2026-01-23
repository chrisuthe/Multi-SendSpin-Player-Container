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
    /// The board's serial number/identifier (if available).
    /// May be null for boards without a serial number.
    /// </summary>
    string? SerialNumber { get; }

    /// <summary>
    /// Number of relay channels on this board.
    /// </summary>
    int ChannelCount { get; }

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

    /// <summary>
    /// Read the actual hardware state of all relay pins (if supported).
    /// This queries the hardware directly rather than returning cached software state.
    /// </summary>
    /// <returns>
    /// Relay state bitmask from hardware (bit 0 = relay 1), or null if:
    /// - Hardware read is not supported by this board type
    /// - Hardware read failed
    /// - Device is not connected
    /// </returns>
    /// <remarks>
    /// FTDI boards support reliable hardware reads. HID boards do not (always returns 0x00).
    /// Use this for diagnostics and verification, not for regular state queries.
    /// </remarks>
    byte? ReadHardwareState() => null;
}
