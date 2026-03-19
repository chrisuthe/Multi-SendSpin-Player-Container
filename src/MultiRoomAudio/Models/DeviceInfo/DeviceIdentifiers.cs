namespace MultiRoomAudio.Models.DeviceInfo;

/// <summary>
/// Stable device identifiers extracted from PulseAudio properties.
/// Used for re-matching devices across reboots when sink names change.
/// </summary>
public record DeviceIdentifiers(
    string? Serial,           // device.serial - most stable if device supports it
    string? BusPath,          // device.bus_path - stable per USB port
    string? VendorId,         // device.vendor.id
    string? ProductId,        // device.product.id
    string? AlsaLongCardName, // alsa.long_card_name - includes USB path info
    // Bluetooth-specific identifiers
    string? BluetoothMac,     // api.bluez5.address - BT MAC address
    string? BluetoothCodec    // bluetooth.codec - current codec (SBC, AAC, aptX, LDAC)
);
