using MultiRoomAudio.Models.DeviceInfo;

namespace MultiRoomAudio.Services.Configuration;

/// <summary>
/// YAML-serializable version of DeviceIdentifiers.
/// </summary>
public class DeviceIdentifiersConfig
{
    public string? Serial { get; set; }
    public string? BusPath { get; set; }
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? AlsaLongCardName { get; set; }
    // Bluetooth-specific
    public string? BluetoothMac { get; set; }
    public string? BluetoothCodec { get; set; }

    /// <summary>
    /// Create from the model record.
    /// </summary>
    public static DeviceIdentifiersConfig? FromModel(DeviceIdentifiers? identifiers)
    {
        if (identifiers == null)
            return null;
        return new DeviceIdentifiersConfig
        {
            Serial = identifiers.Serial,
            BusPath = identifiers.BusPath,
            VendorId = identifiers.VendorId,
            ProductId = identifiers.ProductId,
            AlsaLongCardName = identifiers.AlsaLongCardName,
            BluetoothMac = identifiers.BluetoothMac,
            BluetoothCodec = identifiers.BluetoothCodec
        };
    }

    /// <summary>
    /// Convert to the model record.
    /// </summary>
    public DeviceIdentifiers ToModel() => new(Serial, BusPath, VendorId, ProductId, AlsaLongCardName, BluetoothMac, BluetoothCodec);
}
