using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Mock implementation of relay device enumeration.
/// Returns simulated relay devices for testing without real hardware.
/// Configuration is loaded from MockHardwareConfigService when available.
/// </summary>
public class MockRelayDeviceEnumerator : IRelayDeviceEnumerator
{
    private readonly ILogger<MockRelayDeviceEnumerator> _logger;
    private readonly MockHardwareConfigService? _configService;

    public MockRelayDeviceEnumerator(
        ILogger<MockRelayDeviceEnumerator> logger,
        MockHardwareConfigService? configService = null)
    {
        _logger = logger;
        _configService = configService;

        var boardCount = _configService?.GetEnabledRelayBoards().Count ?? 0;
        _logger.LogInformation(
            "Mock relay device enumerator initialized with {Count} boards (config: {Source})",
            boardCount,
            _configService?.UsingDefaults == true ? "defaults" : "mock_hardware.yaml");
    }

    /// <inheritdoc />
    public bool IsHardwareAvailable => true; // Always available in mock mode

    /// <inheritdoc />
    public List<FtdiDeviceInfo> GetFtdiDevices()
    {
        var boards = _configService?.GetEnabledRelayBoards() ?? new List<MockRelayBoardConfig>();

        // Filter to FTDI boards only
        var ftdiBoards = boards
            .Where(b => b.GetBoardType() == RelayBoardType.Ftdi)
            .Select((b, index) => new FtdiDeviceInfo(
                Index: index,
                SerialNumber: b.SerialNumber,
                Description: b.Description ?? $"Mock FTDI Relay Board ({b.ChannelCount} ch)",
                IsOpen: false,
                UsbPath: b.UsbPath
            ))
            .ToList();

        _logger.LogDebug("Returning {Count} mock FTDI devices", ftdiBoards.Count);
        return ftdiBoards;
    }

    /// <inheritdoc />
    public List<RelayDeviceInfo> GetAllDevices()
    {
        var boards = _configService?.GetEnabledRelayBoards() ?? new List<MockRelayBoardConfig>();

        var devices = boards.Select(b => new RelayDeviceInfo(
            BoardId: b.BoardId,
            BoardType: b.GetBoardType(),
            SerialNumber: b.SerialNumber,
            Description: b.Description ?? $"Mock Relay Board ({b.ChannelCount} ch)",
            ChannelCount: b.ChannelCount,
            IsInUse: false,
            UsbPath: b.UsbPath,
            IsPathBased: !string.IsNullOrEmpty(b.UsbPath) && string.IsNullOrEmpty(b.SerialNumber),
            ChannelCountDetected: b.ChannelCountDetected
        )).ToList();

        _logger.LogDebug("Returning {Count} mock relay devices", devices.Count);
        return devices;
    }
}
