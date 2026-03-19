using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.TriggerModels;

/// <summary>
/// Configuration for a single relay board.
/// </summary>
public class TriggerBoardConfiguration
{
    /// <summary>
    /// Unique identifier for this board.
    /// For FTDI: serial number or "USB:{path}".
    /// For HID: "HID:{serial}" or "HID:{path-hash}".
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
    /// Type of relay board hardware.
    /// </summary>
    public RelayBoardType BoardType { get; set; } = RelayBoardType.Unknown;

    /// <summary>
    /// Number of relay channels on the board (1, 2, 4, 8, or 16).
    /// For HID boards, this is auto-detected and may be updated on connection.
    /// </summary>
    public int ChannelCount { get; set; } = 8;

    /// <summary>
    /// USB port path for boards identified by port (e.g., "1-2.3").
    /// Only used when board has no unique serial number.
    /// </summary>
    public string? UsbPath { get; set; }

    /// <summary>
    /// What to do with relays when the board connects on startup.
    /// Default is AllOff for safety - amplifiers won't unexpectedly power on.
    /// </summary>
    public RelayStartupBehavior StartupBehavior { get; set; } = RelayStartupBehavior.AllOff;

    /// <summary>
    /// What to do with relays when the service shuts down (graceful stop).
    /// Default is AllOff for safety - amplifiers will power off when container stops.
    /// </summary>
    public RelayStartupBehavior ShutdownBehavior { get; set; } = RelayStartupBehavior.AllOff;

    /// <summary>
    /// Configuration for each trigger channel on this board.
    /// </summary>
    public List<TriggerConfiguration> Triggers { get; set; } = new();
}
