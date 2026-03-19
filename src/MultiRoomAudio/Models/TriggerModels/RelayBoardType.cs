namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Type of relay board hardware.
/// </summary>
public enum RelayBoardType
{
    /// <summary>Unknown board type.</summary>
    Unknown,
    /// <summary>
    /// Denkovi FTDI-based relay board using synchronous bitbang mode.
    /// Only Denkovi DAE-CB/Ro4-USB and DAE-CB/Ro8-USB models are supported.
    /// Uses FT245RL chip with sync bitbang protocol (mode 0x04).
    /// </summary>
    Ftdi,
    /// <summary>USB HID relay board (DCT Tech, ucreatefun, etc.) - uses HID protocol.</summary>
    UsbHid,
    /// <summary>Modbus ASCII relay board (Sainsmart, etc.) - uses serial protocol over CH340/CH341.</summary>
    Modbus,
    /// <summary>LCUS binary relay board (1-8 channel) - uses simple binary protocol over CH340/CH341.</summary>
    Lcus,
    /// <summary>Mock board for testing.</summary>
    Mock
}
