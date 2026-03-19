namespace MultiRoomAudio.Models.MockHardwareModels;

/// <summary>
/// Configuration for a mock audio device (PulseAudio sink).
/// </summary>
public class MockAudioDeviceConfig
{
    /// <summary>
    /// PulseAudio sink name (e.g., "alsa_output.usb-Vendor_Product-00.analog-stereo").
    /// Required.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Whether this device is "connected" and visible.
    /// Set to false to simulate a disconnected device.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Display name for the device.
    /// Required.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Device description (typically the product name).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// USB vendor ID (e.g., "30be" for Schiit).
    /// </summary>
    public string? VendorId { get; set; }

    /// <summary>
    /// USB product ID (e.g., "0101").
    /// </summary>
    public string? ProductId { get; set; }

    /// <summary>
    /// Sysfs bus path for stable device identification.
    /// </summary>
    public string? BusPath { get; set; }

    /// <summary>
    /// Device serial number.
    /// </summary>
    public string? Serial { get; set; }

    /// <summary>
    /// Whether this is the default audio output device.
    /// Only one device should have this set to true.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Maximum channel count (2 = stereo, 6 = 5.1, 8 = 7.1).
    /// </summary>
    public int MaxChannels { get; set; } = 2;

    /// <summary>
    /// PulseAudio device index.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Card index this device belongs to.
    /// Links the device to a MockAudioCardConfig by its Index.
    /// </summary>
    public int? CardIndex { get; set; }

    /// <summary>
    /// Bluetooth MAC address (e.g., "00:1A:7D:DA:71:13").
    /// Only for Bluetooth devices.
    /// </summary>
    public string? BluetoothMac { get; set; }

    /// <summary>
    /// Bluetooth codec in use (e.g., "sbc", "aac", "aptx", "ldac").
    /// Only for Bluetooth devices.
    /// </summary>
    public string? BluetoothCodec { get; set; }
}
