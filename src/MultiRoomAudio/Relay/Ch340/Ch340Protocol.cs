namespace MultiRoomAudio.Relay.Ch340;

/// <summary>
/// Detected CH340 relay board protocol type.
/// </summary>
public enum Ch340Protocol
{
    /// <summary>Device did not respond to any relay protocol.</summary>
    Unknown,

    /// <summary>Device responds to Modbus ASCII protocol (Sainsmart 16-channel, etc.).</summary>
    Modbus,

    /// <summary>Device responds to LCUS binary protocol (LCUS 1-8 channel boards).</summary>
    Lcus
}
