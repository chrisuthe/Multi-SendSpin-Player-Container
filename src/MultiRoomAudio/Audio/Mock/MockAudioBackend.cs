using MultiRoomAudio.Models;
using MultiRoomAudio.Services;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace MultiRoomAudio.Audio.Mock;

/// <summary>
/// Mock audio backend for testing without real hardware.
/// Provides fake audio devices that simulate USB DACs with multiple profiles.
/// Channel counts are dynamically determined based on the active card profile.
/// Also recognizes custom sinks created through the CustomSinksService.
/// </summary>
public class MockAudioBackend : IBackend
{
    private readonly ILogger<MockAudioBackend> _logger;
    private readonly CustomSinksService? _customSinksService;
    private readonly Dictionary<string, int> _volumes = new();

    /// <summary>
    /// Pre-configured mock devices simulating real hardware.
    /// IDs match PulseAudio sink naming conventions.
    /// BusPath values use Linux sysfs format matching real PulseAudio device.bus_path property.
    /// </summary>
    public static readonly List<MockDeviceConfig> MockDeviceConfigs = new()
    {
        // Intel HDA onboard audio (PCI device)
        new(
            Id: "alsa_output.pci-0000_00_1f.3.analog-stereo",
            Name: "Built-in Audio Analog Stereo",
            Description: "Built-in Audio",
            VendorId: "8086", ProductId: "a170",
            BusPath: "/devices/pci0000:00/0000:00:1f.3/sound/card0",
            Serial: null,
            Index: 0, IsDefault: true),

        // ASUS Xonar DX 7.1 (PCI device)
        new(
            Id: "alsa_output.pci-0000_05_04.0.analog-surround-71",
            Name: "Xonar DX Analog Surround 7.1",
            Description: "CMI8788 [Oxygen HD Audio]",
            VendorId: "1043", ProductId: "8275",
            BusPath: "/devices/pci0000:00/0000:00:1c.4/0000:05:04.0/sound/card1",
            Serial: null,
            Index: 1, IsDefault: false),

        // Schiit Modi 3 USB DAC
        new(
            Id: "alsa_output.usb-Schiit_Audio_Schiit_Modi_3-00.analog-stereo",
            Name: "Schiit Modi 3 Analog Stereo",
            Description: "Schiit Modi 3",
            VendorId: "30be", ProductId: "0101",
            BusPath: "/devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2:1.0/sound/card2",
            Serial: "0001",
            Index: 2, IsDefault: false),

        // Focusrite Scarlett 2i2 USB audio interface
        new(
            Id: "alsa_output.usb-Focusrite_Scarlett_2i2_USB-00.analog-stereo",
            Name: "Scarlett 2i2 USB Analog Stereo",
            Description: "Scarlett 2i2 USB",
            VendorId: "1235", ProductId: "8210",
            BusPath: "/devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/sound/card3",
            Serial: "Y7XXXXXX00XXXX",
            Index: 3, IsDefault: false),

        // JBL Flip 5 Bluetooth speaker (no sysfs bus_path for BT)
        new(
            Id: "bluez_sink.00_1A_7D_DA_71_13.a2dp_sink",
            Name: "JBL Flip 5",
            Description: "JBL Flip 5",
            VendorId: null, ProductId: null,
            BusPath: null,
            Serial: "00:1A:7D:DA:71:13",
            Index: 4, IsDefault: false),

        // Sony WH-1000XM4 Bluetooth headphones (no sysfs bus_path for BT)
        new(
            Id: "bluez_sink.38_18_4C_E9_85_B2.a2dp_sink",
            Name: "WH-1000XM4",
            Description: "WH-1000XM4",
            VendorId: null, ProductId: null,
            BusPath: null,
            Serial: "38:18:4C:E9:85:B2",
            Index: 5, IsDefault: false),

        // NVIDIA HDMI audio (PCI device - GPU)
        new(
            Id: "alsa_output.pci-0000_01_00.1.hdmi-stereo",
            Name: "HDA NVidia Digital Stereo (HDMI)",
            Description: "HDA NVidia",
            VendorId: "10de", ProductId: "0fb9",
            BusPath: "/devices/pci0000:00/0000:00:01.0/0000:01:00.1/sound/card6",
            Serial: null,
            Index: 6, IsDefault: false),
    };

    public record MockDeviceConfig(
        string Id,
        string Name,
        string Description,
        string? VendorId,
        string? ProductId,
        string? BusPath,
        string? Serial,
        int Index,
        bool IsDefault);

    public MockAudioBackend(ILogger<MockAudioBackend> logger, CustomSinksService? customSinksService = null)
    {
        _logger = logger;
        _customSinksService = customSinksService;
        _logger.LogInformation("Mock audio backend initialized with {Count} devices", MockDeviceConfigs.Count);
    }

    /// <summary>
    /// Gets the channel count for a profile name.
    /// Handles real PulseAudio profile naming conventions.
    /// </summary>
    private static int GetChannelCountForProfile(string? profileName)
    {
        if (string.IsNullOrEmpty(profileName))
            return 2;

        // Extract output portion if duplex profile (e.g., "output:analog-stereo+input:analog-stereo")
        var outputProfile = profileName.Contains('+')
            ? profileName.Split('+')[0]
            : profileName;

        return outputProfile.ToLowerInvariant() switch
        {
            // Surround profiles
            "output:analog-surround-71" => 8,
            "output:hdmi-surround71" => 8,
            "output:analog-surround-51" => 6,
            "output:hdmi-surround" => 6,

            // Stereo profiles
            "output:analog-stereo" => 2,
            "output:iec958-stereo" => 2,
            "output:hdmi-stereo" => 2,
            "output:hdmi-stereo-extra1" => 2,

            // Bluetooth profiles
            "a2dp-sink" => 2,
            "headset-head-unit" => 1, // HSP/HFP is mono

            "off" => 0,
            _ => 2 // Default to stereo
        };
    }

    /// <inheritdoc />
    public string Name => "mock";

    /// <inheritdoc />
    public IEnumerable<AudioDevice> GetOutputDevices()
    {
        var devices = CreateMockDevices();
        _logger.LogDebug("Returning {Count} mock audio devices", devices.Count);
        return devices;
    }

    /// <inheritdoc />
    public AudioDevice? GetDevice(string deviceId)
    {
        var devices = CreateMockDevices();

        // Try by index
        if (int.TryParse(deviceId, out var index))
        {
            return devices.FirstOrDefault(d => d.Index == index);
        }

        // Try by exact ID match only (no partial name matching)
        var device = devices.FirstOrDefault(d =>
            d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));

        if (device != null)
            return device;

        // Check if it's a custom sink
        var customSink = _customSinksService?.GetSink(deviceId);
        if (customSink != null)
        {
            _logger.LogDebug("Found custom sink '{SinkName}' as mock device", deviceId);
            return CreateMockDeviceForCustomSink(customSink);
        }

        return null;
    }

    /// <summary>
    /// Creates a mock AudioDevice for a custom sink.
    /// </summary>
    private AudioDevice CreateMockDeviceForCustomSink(CustomSinkResponse sink)
    {
        return new AudioDevice(
            Index: 100 + Math.Abs(sink.Name.GetHashCode() % 100), // Generate unique index
            Id: sink.Name,
            Name: sink.Description ?? sink.Name,
            MaxChannels: 2,
            DefaultSampleRate: 48000,
            DefaultLowLatencyMs: 20,
            DefaultHighLatencyMs: 100,
            IsDefault: false,
            Capabilities: new DeviceCapabilities(
                SupportedSampleRates: new[] { 44100, 48000, 96000, 192000 },
                SupportedBitDepths: new[] { 16, 24, 32 },
                MaxChannels: 2,
                PreferredSampleRate: 48000,
                PreferredBitDepth: 24
            ),
            Identifiers: new DeviceIdentifiers(
                Serial: null,
                BusPath: null,
                VendorId: null,
                ProductId: null,
                AlsaLongCardName: $"Custom {sink.Type} Sink"
            )
        );
    }

    /// <inheritdoc />
    public AudioDevice? GetDefaultDevice()
    {
        return CreateMockDevices().FirstOrDefault(d => d.IsDefault);
    }

    /// <inheritdoc />
    public DeviceCapabilities? GetDeviceCapabilities(string? deviceId)
    {
        // Get the device to determine its current channel count
        var device = string.IsNullOrEmpty(deviceId) ? GetDefaultDevice() : GetDevice(deviceId);
        var maxChannels = device?.MaxChannels ?? 2;

        return new DeviceCapabilities(
            SupportedSampleRates: new[] { 44100, 48000, 88200, 96000, 176400, 192000 },
            SupportedBitDepths: new[] { 16, 24, 32 },
            MaxChannels: maxChannels,
            PreferredSampleRate: 48000,
            PreferredBitDepth: 24
        );
    }

    /// <inheritdoc />
    public bool ValidateDevice(string? deviceId, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            errorMessage = null;
            return true;
        }

        var device = GetDevice(deviceId);
        if (device == null)
        {
            errorMessage = $"Mock device '{deviceId}' not found.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <inheritdoc />
    public void RefreshDevices()
    {
        _logger.LogDebug("Mock device refresh (no-op)");
    }

    /// <inheritdoc />
    public IAudioPlayer CreatePlayer(string? deviceId, ILoggerFactory loggerFactory)
    {
        var device = string.IsNullOrEmpty(deviceId)
            ? GetDefaultDevice()
            : GetDevice(deviceId);

        // For custom sinks, use the sink name directly even if GetDevice returns null
        var deviceName = device?.Name ?? deviceId ?? "(default)";

        _logger.LogInformation("Creating mock player for device: {Device}", deviceName);

        // Return a null audio player that discards samples
        return new MockAudioPlayer(loggerFactory.CreateLogger<MockAudioPlayer>(), deviceName);
    }

    /// <inheritdoc />
    public Task<bool> SetVolumeAsync(string? deviceId, int volume, CancellationToken cancellationToken = default)
    {
        var device = string.IsNullOrEmpty(deviceId) ? GetDefaultDevice() : GetDevice(deviceId);
        if (device == null)
            return Task.FromResult(false);

        _volumes[device.Id] = Math.Clamp(volume, 0, 100);
        _logger.LogDebug("Mock volume for {Device} set to {Volume}%", device.Name, volume);
        return Task.FromResult(true);
    }

    private static List<AudioDevice> CreateMockDevices()
    {
        // Get all cards to determine active profiles and channel counts
        var cards = MockCardEnumerator.GetCards().ToList();

        return MockDeviceConfigs.Select(config =>
        {
            // Find the corresponding card by index
            var card = cards.FirstOrDefault(c => c.Index == config.Index);
            var channelCount = GetChannelCountForProfile(card?.ActiveProfile);

            // Bluetooth devices have different sample rate support
            // Detect Bluetooth from device ID (bluez_sink prefix) - matches real PulseAudio naming
            var isBluetooth = config.Id.StartsWith("bluez_", StringComparison.OrdinalIgnoreCase);
            var sampleRates = isBluetooth
                ? new[] { 44100, 48000 }
                : new[] { 44100, 48000, 96000, 192000 };
            var bitDepths = isBluetooth
                ? new[] { 16 }
                : new[] { 16, 24, 32 };

            return new AudioDevice(
                Index: config.Index,
                Id: config.Id,
                Name: config.Name,
                MaxChannels: channelCount,
                DefaultSampleRate: 48000,
                DefaultLowLatencyMs: isBluetooth ? 50 : 20,
                DefaultHighLatencyMs: isBluetooth ? 200 : 100,
                IsDefault: config.IsDefault,
                Capabilities: new DeviceCapabilities(
                    SupportedSampleRates: sampleRates,
                    SupportedBitDepths: bitDepths,
                    MaxChannels: channelCount,
                    PreferredSampleRate: 48000,
                    PreferredBitDepth: isBluetooth ? 16 : 24
                ),
                Identifiers: new DeviceIdentifiers(
                    Serial: config.Serial,
                    BusPath: config.BusPath,
                    VendorId: config.VendorId,
                    ProductId: config.ProductId,
                    AlsaLongCardName: config.Description
                )
            );
        }).ToList();
    }
}

/// <summary>
/// Mock audio player that discards audio samples.
/// Used when MOCK_HARDWARE is enabled for testing without real audio output.
/// Implements the full IAudioPlayer interface from SendSpin.SDK.
/// </summary>
public class MockAudioPlayer : IAudioPlayer
{
    private readonly ILogger<MockAudioPlayer> _logger;
    private readonly string _deviceName;
    private IAudioSampleSource? _sampleSource;
    private AudioFormat? _currentFormat;
    private volatile bool _disposed;
    private Timer? _playbackTimer;

    public MockAudioPlayer(ILogger<MockAudioPlayer> logger, string? deviceName)
    {
        _logger = logger;
        _deviceName = deviceName ?? "default";
    }

    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    public float Volume { get; set; } = 1.0f;

    public bool IsMuted { get; set; }

    public int OutputLatencyMs { get; private set; } = 50;

    public event EventHandler<AudioPlayerState>? StateChanged;
#pragma warning disable CS0067 // Event is never used (required by IAudioPlayer interface)
    public event EventHandler<AudioPlayerError>? ErrorOccurred;
#pragma warning restore CS0067

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _currentFormat = format;
        State = AudioPlayerState.Stopped;
        StateChanged?.Invoke(this, State);

        _logger.LogInformation(
            "Mock player initialized: {SampleRate}Hz, {Channels}ch, device: {Device}",
            format.SampleRate, format.Channels, _deviceName);

        return Task.CompletedTask;
    }

    public void SetSampleSource(IAudioSampleSource source)
    {
        _sampleSource = source;
        _logger.LogDebug("Sample source set for mock player");
    }

    public void Play()
    {
        if (State == AudioPlayerState.Playing)
            return;

        State = AudioPlayerState.Playing;
        StateChanged?.Invoke(this, State);

        // Start a timer to simulate reading samples (to keep the SDK happy)
        _playbackTimer?.Dispose();
        _playbackTimer = new Timer(SimulatePlayback, null, 0, 20);

        _logger.LogInformation("Mock player started for {Device}", _deviceName);
    }

    public void Pause()
    {
        if (State != AudioPlayerState.Playing)
            return;

        _playbackTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        State = AudioPlayerState.Paused;
        StateChanged?.Invoke(this, State);

        _logger.LogInformation("Mock player paused for {Device}", _deviceName);
    }

    public void Stop()
    {
        _playbackTimer?.Dispose();
        _playbackTimer = null;

        State = AudioPlayerState.Stopped;
        StateChanged?.Invoke(this, State);

        _logger.LogDebug("Mock player stopped for {Device}", _deviceName);
    }

    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock player switching device to: {Device}", deviceId ?? "default");
        return Task.CompletedTask;
    }

    private void SimulatePlayback(object? state)
    {
        if (_disposed || State != AudioPlayerState.Playing || _sampleSource == null)
            return;

        // Read and discard samples to keep the SDK buffer flowing
        var buffer = new float[1024];
        try
        {
            var read = _sampleSource.Read(buffer, 0, buffer.Length);
            // Samples are discarded - this is a null sink
        }
        catch
        {
            // Ignore errors in mock player
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        Stop();

        _logger.LogDebug("Mock player disposed for {Device}", _deviceName);
        return ValueTask.CompletedTask;
    }
}
