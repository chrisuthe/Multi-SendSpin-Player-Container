using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Interface for enumerating available relay board devices.
/// Implementations handle the platform-specific discovery of FTDI and HID relay boards.
/// </summary>
public interface IRelayDeviceEnumerator
{
    /// <summary>
    /// Whether any relay hardware support is available on this system.
    /// For real hardware, this checks if FTDI library or HID support is present.
    /// For mock mode, this always returns true.
    /// </summary>
    bool IsHardwareAvailable { get; }

    /// <summary>
    /// Get list of available FTDI devices (legacy API for backward compatibility).
    /// </summary>
    List<FtdiDeviceInfo> GetFtdiDevices();

    /// <summary>
    /// Get unified list of all available relay devices (FTDI and HID).
    /// </summary>
    List<RelayDeviceInfo> GetAllDevices();
}
