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
/// Device configuration is loaded from MockHardwareConfigService.
/// </summary>
public class MockAudioBackend : IBackend
{
    private readonly ILogger<MockAudioBackend> _logger;
    private readonly CustomSinksService? _customSinksService;
    private readonly MockHardwareConfigService? _mockConfigService;
    private readonly Dictionary<string, int> _volumes = new();

    public MockAudioBackend(
        ILogger<MockAudioBackend> logger,
        CustomSinksService? customSinksService = null,
        MockHardwareConfigService? mockConfigService = null)
    {
        _logger = logger;
        _customSinksService = customSinksService;
        _mockConfigService = mockConfigService;

        var deviceCount = _mockConfigService?.GetEnabledAudioDevices().Count ?? 0;
        _logger.LogInformation(
            "Mock audio backend initialized with {Count} devices (config: {ConfigSource})",
            deviceCount,
            _mockConfigService?.UsingDefaults == true ? "defaults" : "mock_hardware.yaml");
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

    private List<AudioDevice> CreateMockDevices()
    {
        // Get enabled devices from config service
        var deviceConfigs = _mockConfigService?.GetEnabledAudioDevices()
            ?? new List<MockAudioDeviceConfig>();

        // Get all cards to determine active profiles and channel counts
        var cards = MockCardEnumerator.GetCards().ToList();

        return deviceConfigs.Select(config =>
        {
            // Find the corresponding card by CardIndex
            var card = config.CardIndex.HasValue
                ? cards.FirstOrDefault(c => c.Index == config.CardIndex.Value)
                : null;
            var channelCount = card != null
                ? GetChannelCountForProfile(card.ActiveProfile)
                : config.MaxChannels;

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
                ),
                CardIndex: config.CardIndex
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
