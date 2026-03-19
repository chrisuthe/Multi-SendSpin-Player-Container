using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Request to add a new relay board.
/// </summary>
public class AddBoardRequest
{
    /// <summary>
    /// Board identifier.
    /// For FTDI: serial number or "USB:{path}".
    /// For HID: "HID:{serial}" (auto-generated from device enumeration).
    /// For Modbus: "MODBUS:{port}" (e.g., "MODBUS:/dev/ttyUSB0").
    /// </summary>
    [Required]
    public string BoardId { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly display name for this board.
    /// </summary>
    [StringLength(100)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Type of relay board (Ftdi or UsbHid). If not specified, inferred from BoardId.
    /// </summary>
    public RelayBoardType BoardType { get; set; } = RelayBoardType.Unknown;

    /// <summary>
    /// Number of relay channels on the board (1, 2, 4, 8, or 16).
    /// For HID boards with detectable channel count, this may be auto-updated.
    /// </summary>
    public int ChannelCount { get; set; } = 8;
}
