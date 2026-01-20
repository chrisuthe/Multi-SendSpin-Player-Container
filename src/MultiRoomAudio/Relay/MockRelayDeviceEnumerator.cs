using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Mock implementation of relay device enumeration.
/// Returns a set of simulated relay devices for testing without real hardware.
/// </summary>
public class MockRelayDeviceEnumerator : IRelayDeviceEnumerator
{
    private readonly ILogger<MockRelayDeviceEnumerator> _logger;

    /// <summary>
    /// Pre-configured mock FTDI devices for testing.
    /// </summary>
    public static readonly List<FtdiDeviceInfo> MockFtdiDevices = new()
    {
        new FtdiDeviceInfo(
            Index: 0,
            SerialNumber: "MOCK001",
            Description: "Mock 8-Channel FTDI Relay Board",
            IsOpen: false
        ),
        new FtdiDeviceInfo(
            Index: 1,
            SerialNumber: "MOCK002",
            Description: "Mock 8-Channel FTDI Relay Board",
            IsOpen: false
        ),
        new FtdiDeviceInfo(
            Index: 2,
            SerialNumber: null,
            Description: "Mock FTDI Board (No Serial)",
            IsOpen: false,
            UsbPath: "1-2.3"
        )
    };

    /// <summary>
    /// Pre-configured mock relay devices (all types) for testing.
    /// </summary>
    public static readonly List<RelayDeviceInfo> MockAllDevices = new()
    {
        // FTDI boards
        new RelayDeviceInfo(
            BoardId: "MOCK001",
            BoardType: RelayBoardType.Ftdi,
            SerialNumber: "MOCK001",
            Description: "Mock 8-Channel FTDI Relay Board",
            ChannelCount: 8,
            IsInUse: false,
            UsbPath: null,
            IsPathBased: false,
            ChannelCountDetected: false // FTDI boards need manual config
        ),
        new RelayDeviceInfo(
            BoardId: "MOCK002",
            BoardType: RelayBoardType.Ftdi,
            SerialNumber: "MOCK002",
            Description: "Mock 8-Channel FTDI Relay Board",
            ChannelCount: 8,
            IsInUse: false,
            UsbPath: null,
            IsPathBased: false,
            ChannelCountDetected: false
        ),

        // HID board with detectable channel count (product name "USBRelay4")
        new RelayDeviceInfo(
            BoardId: "HID:QAAMZ",
            BoardType: RelayBoardType.UsbHid,
            SerialNumber: "QAAMZ",
            Description: "USBRelay4", // Channel count detectable from name
            ChannelCount: 4,
            IsInUse: false,
            UsbPath: null,
            IsPathBased: false,
            ChannelCountDetected: true // Auto-detected - don't allow editing
        ),

        // HID board without detectable channel count (generic product name)
        new RelayDeviceInfo(
            BoardId: "HID:ABCDE",
            BoardType: RelayBoardType.UsbHid,
            SerialNumber: "ABCDE",
            Description: "Generic HID Relay", // Can't detect channel count from name
            ChannelCount: 8, // Default guess
            IsInUse: false,
            UsbPath: null,
            IsPathBased: false,
            ChannelCountDetected: false // User must configure
        )
    };

    public MockRelayDeviceEnumerator(ILogger<MockRelayDeviceEnumerator> logger)
    {
        _logger = logger;
        _logger.LogInformation("Mock relay device enumerator initialized with {FtdiCount} FTDI and {TotalCount} total devices",
            MockFtdiDevices.Count, MockAllDevices.Count);
    }

    /// <inheritdoc />
    public bool IsHardwareAvailable => true; // Always available in mock mode

    /// <inheritdoc />
    public List<FtdiDeviceInfo> GetFtdiDevices()
    {
        _logger.LogDebug("Returning {Count} mock FTDI devices", MockFtdiDevices.Count);
        // Return copies so IsOpen state doesn't persist
        return MockFtdiDevices.Select(d => d with { }).ToList();
    }

    /// <inheritdoc />
    public List<RelayDeviceInfo> GetAllDevices()
    {
        _logger.LogDebug("Returning {Count} mock relay devices", MockAllDevices.Count);
        // Return copies so IsInUse state doesn't persist
        return MockAllDevices.Select(d => d with { }).ToList();
    }
}
