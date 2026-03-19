using MultiRoomAudio.Models.TriggerModels;

namespace MultiRoomAudio.Models.MockHardwareModels;

/// <summary>
/// Configuration for a mock relay board.
/// </summary>
public class MockRelayBoardConfig
{
    /// <summary>
    /// Board identifier (e.g., "MOCK001" for FTDI, "HID:QAAMZ" for USB HID).
    /// Required.
    /// </summary>
    public string BoardId { get; set; } = string.Empty;

    /// <summary>
    /// Whether this board is "connected" and visible.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Board type: "ftdi", "usb_hid", or "modbus".
    /// Required.
    /// </summary>
    public string BoardType { get; set; } = "ftdi";

    /// <summary>
    /// Board serial number.
    /// </summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Board description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Number of relay channels (1-16).
    /// Required.
    /// </summary>
    public int ChannelCount { get; set; } = 8;

    /// <summary>
    /// Whether the channel count was auto-detected from the device.
    /// If true, the UI won't allow editing the channel count.
    /// </summary>
    public bool ChannelCountDetected { get; set; }

    /// <summary>
    /// USB port path (e.g., "1-2.3") for boards without serial numbers.
    /// </summary>
    public string? UsbPath { get; set; }

    /// <summary>
    /// Get the RelayBoardType enum value from the string board type.
    /// </summary>
    public RelayBoardType GetBoardType()
    {
        return BoardType?.ToLowerInvariant() switch
        {
            "ftdi" => RelayBoardType.Ftdi,
            "usb_hid" or "usbhid" or "hid" => RelayBoardType.UsbHid,
            "modbus" or "ch340" or "ch341" or "serial" => RelayBoardType.Modbus,
            "mock" => RelayBoardType.Mock,
            _ => RelayBoardType.Unknown
        };
    }
}
