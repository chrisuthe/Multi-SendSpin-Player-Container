using MultiRoomAudio.Relay;

namespace MultiRoomAudio.Models;

/// <summary>
/// Root configuration for mock hardware.
/// When mock_hardware.yaml exists, this completely replaces all hardcoded defaults.
/// </summary>
public class MockHardwareConfiguration
{
    /// <summary>
    /// Mock audio devices (PulseAudio sinks).
    /// </summary>
    public List<MockAudioDeviceConfig> AudioDevices { get; set; } = new();

    /// <summary>
    /// Mock audio cards with profile management.
    /// </summary>
    public List<MockAudioCardConfig> AudioCards { get; set; } = new();

    /// <summary>
    /// Mock relay boards (FTDI and USB HID).
    /// </summary>
    public List<MockRelayBoardConfig> RelayBoards { get; set; } = new();
}

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

/// <summary>
/// Configuration for a card profile.
/// </summary>
public class MockCardProfileConfig
{
    /// <summary>
    /// Profile name (e.g., "output:analog-stereo").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Profile description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Number of sinks this profile provides.
    /// </summary>
    public int Sinks { get; set; } = 1;

    /// <summary>
    /// Profile priority (higher = preferred).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether this profile is available.
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Whether this is the active/default profile.
    /// </summary>
    public bool IsDefault { get; set; }
}

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
