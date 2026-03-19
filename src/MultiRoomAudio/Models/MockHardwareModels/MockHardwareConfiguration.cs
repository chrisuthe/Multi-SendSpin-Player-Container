namespace MultiRoomAudio.Models.MockHardwareModels;

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
