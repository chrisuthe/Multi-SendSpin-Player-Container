using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models;

/// <summary>
/// State of the relay trigger feature.
/// </summary>
public enum TriggerFeatureState
{
    /// <summary>Feature is disabled.</summary>
    Disabled,
    /// <summary>Feature is enabled but relay board not connected.</summary>
    Disconnected,
    /// <summary>Feature is enabled and relay board is connected.</summary>
    Connected,
    /// <summary>Feature is enabled but encountered an error.</summary>
    Error
}

/// <summary>
/// State of an individual relay channel.
/// </summary>
public enum RelayState
{
    /// <summary>Relay is off (NO contacts open).</summary>
    Off,
    /// <summary>Relay is on (NO contacts closed).</summary>
    On,
    /// <summary>Relay state is unknown.</summary>
    Unknown
}

/// <summary>
/// Type of relay board hardware.
/// </summary>
public enum RelayBoardType
{
    /// <summary>Unknown board type.</summary>
    Unknown,
    /// <summary>FTDI-based relay board (Denkovi, etc.) - uses serial protocol.</summary>
    Ftdi,
    /// <summary>USB HID relay board (DCT Tech, ucreatefun, etc.) - uses HID protocol.</summary>
    UsbHid,
    /// <summary>Mock board for testing.</summary>
    Mock
}

/// <summary>
/// What to do with relays when the board connects on startup.
/// </summary>
public enum RelayStartupBehavior
{
    /// <summary>Turn all relays OFF on startup (safest default).</summary>
    AllOff,
    /// <summary>Turn all relays ON on startup.</summary>
    AllOn,
    /// <summary>Don't change relay state on startup (preserve hardware state).</summary>
    NoChange
}

/// <summary>
/// Valid channel count options for relay boards.
/// </summary>
public static class ValidChannelCounts
{
    public static readonly int[] Values = { 1, 2, 4, 8, 16 };

    public static bool IsValid(int count) => Values.Contains(count);

    public static int Clamp(int count)
    {
        if (count <= 1) return 1;
        if (count <= 2) return 2;
        if (count <= 4) return 4;
        if (count <= 8) return 8;
        return 16;
    }
}

/// <summary>
/// Configuration for a single trigger channel (1-16).
/// Maps a relay to a custom sink with configurable off-delay.
/// </summary>
public class TriggerConfiguration
{
    /// <summary>
    /// Channel number (1-16).
    /// </summary>
    [Range(1, 16)]
    public int Channel { get; set; }

    /// <summary>
    /// Name of the custom sink that triggers this relay.
    /// Null or empty means this trigger is not assigned.
    /// </summary>
    public string? CustomSinkName { get; set; }

    /// <summary>
    /// Delay in seconds before turning off the relay after playback stops.
    /// Default is 60 seconds (1 minute).
    /// </summary>
    [Range(0, 3600)]
    public int OffDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Optional friendly name for this trigger/zone.
    /// </summary>
    [StringLength(100)]
    public string? ZoneName { get; set; }
}

/// <summary>
/// Configuration for a single relay board.
/// </summary>
public class TriggerBoardConfiguration
{
    /// <summary>
    /// Unique identifier for this board.
    /// For FTDI: serial number or "USB:{path}".
    /// For HID: "HID:{serial}" or "HID:{path-hash}".
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

/// <summary>
/// Root configuration for the trigger feature.
/// Supports multiple relay boards.
/// </summary>
public class TriggerFeatureConfiguration
{
    /// <summary>
    /// Whether the trigger feature is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// List of configured relay boards.
    /// </summary>
    public List<TriggerBoardConfiguration> Boards { get; set; } = new();

    // Legacy properties for migration from single-board format
    // These are only used during config load/migration

    /// <summary>
    /// Legacy: Number of relay channels (migrated to Boards[0].ChannelCount).
    /// </summary>
    [Obsolete("Use Boards instead. This property is only for config migration.")]
    public int? ChannelCount { get; set; }

    /// <summary>
    /// Legacy: Serial port device path (no longer used).
    /// </summary>
    [Obsolete("Use Boards instead. This property is only for config migration.")]
    public string? DevicePath { get; set; }

    /// <summary>
    /// Legacy: FTDI serial number (migrated to Boards[0].BoardId).
    /// </summary>
    [Obsolete("Use Boards instead. This property is only for config migration.")]
    public string? FtdiSerialNumber { get; set; }

    /// <summary>
    /// Legacy: Trigger configurations (migrated to Boards[0].Triggers).
    /// </summary>
    [Obsolete("Use Boards instead. This property is only for config migration.")]
    public List<TriggerConfiguration>? Triggers { get; set; }

    /// <summary>
    /// Check if this config uses the legacy single-board format and needs migration.
    /// </summary>
    public bool NeedsMigration
    {
        get
        {
            #pragma warning disable CS0618 // Obsolete - intentional for migration check
            return FtdiSerialNumber != null || (Triggers != null && Triggers.Count > 0);
            #pragma warning restore CS0618
        }
    }

    /// <summary>
    /// Migrate from legacy single-board format to multi-board format.
    /// </summary>
    public void MigrateFromLegacy()
    {
        if (!NeedsMigration) return;

        #pragma warning disable CS0618 // Obsolete - intentional for migration
        var legacyBoard = new TriggerBoardConfiguration
        {
            BoardId = FtdiSerialNumber ?? "LEGACY",
            DisplayName = "Relay Board",
            ChannelCount = ChannelCount ?? 8,
            Triggers = Triggers ?? new List<TriggerConfiguration>()
        };

        Boards = new List<TriggerBoardConfiguration> { legacyBoard };

        // Clear legacy properties
        FtdiSerialNumber = null;
        DevicePath = null;
        ChannelCount = null;
        Triggers = null;
        #pragma warning restore CS0618
    }
}

/// <summary>
/// Response for a single trigger channel status.
/// </summary>
public record TriggerResponse(
    int Channel,
    string? CustomSinkName,
    string? CustomSinkDisplayName,
    int OffDelaySeconds,
    string? ZoneName,
    RelayState RelayState,
    bool IsActive,
    DateTime? LastActivated,
    DateTime? ScheduledOffTime
);

/// <summary>
/// Response for a single relay board status.
/// </summary>
public record TriggerBoardResponse(
    string BoardId,
    string? DisplayName,
    RelayBoardType BoardType,
    bool IsConnected,
    TriggerFeatureState State,
    int ChannelCount,
    string? UsbPath,
    bool IsPortBased,
    string? ErrorMessage,
    List<TriggerResponse> Triggers,
    int CurrentRelayStates,
    RelayStartupBehavior StartupBehavior,
    RelayStartupBehavior ShutdownBehavior
);

/// <summary>
/// Response for the overall trigger feature status.
/// Supports multiple boards.
/// </summary>
public record TriggerFeatureResponse(
    bool Enabled,
    List<TriggerBoardResponse> Boards,
    int TotalChannels
);

/// <summary>
/// Request to enable/disable the trigger feature.
/// </summary>
public class TriggerFeatureEnableRequest
{
    /// <summary>
    /// Whether to enable the trigger feature.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Request to add a new relay board.
/// </summary>
public class AddBoardRequest
{
    /// <summary>
    /// Board identifier.
    /// For FTDI: serial number or "USB:{path}".
    /// For HID: "HID:{serial}" (auto-generated from device enumeration).
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

/// <summary>
/// Request to update a board's settings.
/// </summary>
public class UpdateBoardRequest
{
    /// <summary>
    /// User-friendly display name for this board.
    /// </summary>
    [StringLength(100)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Number of relay channels on the board (1, 2, 4, 8, or 16).
    /// </summary>
    public int? ChannelCount { get; set; }

    /// <summary>
    /// What to do with relays when the board connects on startup.
    /// </summary>
    public RelayStartupBehavior? StartupBehavior { get; set; }

    /// <summary>
    /// What to do with relays when the service shuts down.
    /// </summary>
    public RelayStartupBehavior? ShutdownBehavior { get; set; }
}

/// <summary>
/// Request to configure a single trigger channel.
/// </summary>
public class TriggerConfigureRequest
{
    /// <summary>
    /// Channel number (1-16).
    /// </summary>
    [Required]
    [Range(1, 16, ErrorMessage = "Channel must be between 1 and 16.")]
    public int Channel { get; set; }

    /// <summary>
    /// Custom sink name to assign to this trigger.
    /// Set to null or empty to unassign.
    /// </summary>
    public string? CustomSinkName { get; set; }

    /// <summary>
    /// Off delay in seconds (0-3600).
    /// </summary>
    [Range(0, 3600, ErrorMessage = "Off delay must be between 0 and 3600 seconds.")]
    public int OffDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Optional friendly name for this zone.
    /// </summary>
    [StringLength(100, ErrorMessage = "Zone name must be 100 characters or less.")]
    public string? ZoneName { get; set; }
}

/// <summary>
/// Request to manually control a relay (for testing).
/// </summary>
public class RelayManualControlRequest
{
    /// <summary>
    /// Channel number (1-16).
    /// </summary>
    [Required]
    [Range(1, 16, ErrorMessage = "Channel must be between 1 and 16.")]
    public int Channel { get; set; }

    /// <summary>
    /// Desired relay state.
    /// </summary>
    [Required]
    public bool On { get; set; }
}

/// <summary>
/// Request to update the channel count.
/// </summary>
public class ChannelCountRequest
{
    /// <summary>
    /// Number of relay channels (1, 2, 4, 8, or 16).
    /// </summary>
    [Required]
    public int ChannelCount { get; set; }
}

/// <summary>
/// Information about a detected FTDI device.
/// </summary>
public record FtdiDeviceInfo(
    int Index,
    string? SerialNumber,
    string? Description,
    bool IsOpen,
    string? UsbPath = null
)
{
    /// <summary>
    /// Get the preferred identifier for this device.
    /// Uses serial number if available, otherwise USB path.
    /// </summary>
    public string GetBoardId()
    {
        if (!string.IsNullOrWhiteSpace(SerialNumber))
            return SerialNumber;

        if (!string.IsNullOrWhiteSpace(UsbPath))
            return $"USB:{UsbPath}";

        return $"FTDI-{Index:D2}";
    }

    /// <summary>
    /// Whether this device is identified by USB port path (less stable).
    /// </summary>
    public bool IsPortBased => string.IsNullOrWhiteSpace(SerialNumber);
};

/// <summary>
/// Unified information about a detected relay board device (FTDI or HID).
/// Used for device discovery/enumeration in the UI.
/// </summary>
public record RelayDeviceInfo(
    /// <summary>Board identifier to use when adding this device.</summary>
    string BoardId,
    /// <summary>Type of relay board.</summary>
    RelayBoardType BoardType,
    /// <summary>Serial number if available.</summary>
    string? SerialNumber,
    /// <summary>Product/device description.</summary>
    string? Description,
    /// <summary>Number of channels (auto-detected for HID boards).</summary>
    int ChannelCount,
    /// <summary>Whether the device is currently open/in use.</summary>
    bool IsInUse,
    /// <summary>USB path if available.</summary>
    string? UsbPath,
    /// <summary>Whether this device is identified by path (less stable).</summary>
    bool IsPathBased,
    /// <summary>Whether the channel count was auto-detected (true) or needs manual config (false).</summary>
    bool ChannelCountDetected = false
)
{
    /// <summary>
    /// Create from an FTDI device info.
    /// </summary>
    public static RelayDeviceInfo FromFtdi(FtdiDeviceInfo ftdi) => new(
        BoardId: ftdi.GetBoardId(),
        BoardType: RelayBoardType.Ftdi,
        SerialNumber: ftdi.SerialNumber,
        Description: ftdi.Description ?? "FTDI Relay Board",
        ChannelCount: 8, // FTDI boards need manual channel count config
        IsInUse: ftdi.IsOpen,
        UsbPath: ftdi.UsbPath,
        IsPathBased: ftdi.IsPortBased,
        ChannelCountDetected: false // FTDI boards always need manual config
    );
};
