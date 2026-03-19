namespace MultiRoomAudio.Models.MockHardwareModels;

/// <summary>
/// Configuration for a mock audio card with profiles.
/// </summary>
public class MockAudioCardConfig
{
    /// <summary>
    /// Card name (e.g., "alsa_card.usb-Vendor_Product-00").
    /// Required.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this card is "connected" and visible.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Card description.
    /// Required.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Driver name (e.g., "module-alsa-card.c").
    /// </summary>
    public string Driver { get; set; } = "module-alsa-card.c";

    /// <summary>
    /// Card index.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Available profiles for this card.
    /// Required.
    /// </summary>
    public List<MockCardProfileConfig> Profiles { get; set; } = new();
}
