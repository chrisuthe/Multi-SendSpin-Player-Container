namespace MultiRoomAudio.Relay.Ch340;

/// <summary>
/// Probe result for CH340 serial devices.
/// </summary>
public enum Ch340ProbeResult
{
    /// <summary>Device is a relay board (Modbus or LCUS protocol detected).</summary>
    RelayBoard,

    /// <summary>Device did not respond to any relay protocol.</summary>
    NoResponse,

    /// <summary>Serial port is busy (in use by another process).</summary>
    PortBusy,

    /// <summary>Error during probe.</summary>
    Error
}
