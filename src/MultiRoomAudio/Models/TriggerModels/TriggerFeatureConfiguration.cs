namespace MultiRoomAudio.Models.TriggerModels;

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
        if (!NeedsMigration)
            return;

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
