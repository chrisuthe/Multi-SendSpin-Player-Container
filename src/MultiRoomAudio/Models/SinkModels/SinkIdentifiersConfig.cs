namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// YAML-serializable stable identifiers for a sink.
/// Used for re-matching sinks when ALSA card numbers change after reboot.
/// </summary>
public class SinkIdentifiersConfig
{
    /// <summary>USB bus path (most stable identifier for USB devices).</summary>
    public string? BusPath { get; set; }

    /// <summary>Device serial number (may not be unique across identical devices).</summary>
    public string? Serial { get; set; }

    /// <summary>USB vendor ID.</summary>
    public string? VendorId { get; set; }

    /// <summary>USB product ID.</summary>
    public string? ProductId { get; set; }

    /// <summary>ALSA long card name (stable for PCIe devices).</summary>
    public string? AlsaLongCardName { get; set; }

    /// <summary>Last known sink name (may become stale after reboot).</summary>
    public string? LastKnownSinkName { get; set; }

    /// <summary>
    /// Card profile name (e.g., "output:analog-surround-71") from card-profiles.yaml.
    /// Used to ensure the correct profile is active when resolving the sink.
    /// </summary>
    public string? CardProfile { get; set; }

    /// <summary>
    /// Checks if this identifier config has at least one stable identifier.
    /// </summary>
    public bool HasStableIdentifier()
    {
        return !string.IsNullOrEmpty(BusPath) ||
               !string.IsNullOrEmpty(AlsaLongCardName) ||
               !string.IsNullOrEmpty(Serial) ||
               (!string.IsNullOrEmpty(VendorId) && !string.IsNullOrEmpty(ProductId));
    }
}
