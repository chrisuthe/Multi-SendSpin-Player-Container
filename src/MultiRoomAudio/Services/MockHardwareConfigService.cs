using MultiRoomAudio.Models;
using MultiRoomAudio.Relay;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiRoomAudio.Services;

/// <summary>
/// Manages mock hardware configuration from YAML file.
/// When mock_hardware.yaml exists, it completely replaces hardcoded defaults.
/// When the file doesn't exist, hardcoded defaults are used.
/// </summary>
public class MockHardwareConfigService
{
    private readonly ILogger<MockHardwareConfigService> _logger;
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private MockHardwareConfiguration _config;
    private bool _usingDefaults;

    public MockHardwareConfigService(
        ILogger<MockHardwareConfigService> logger,
        EnvironmentService environment)
    {
        _logger = logger;
        _configPath = Path.Combine(environment.ConfigPath, "mock_hardware.yaml");

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
            .Build();

        _config = new MockHardwareConfiguration();
        Load();
    }

    /// <summary>
    /// The current mock hardware configuration.
    /// </summary>
    public MockHardwareConfiguration Config
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _config;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Whether the configuration is using hardcoded defaults (no YAML file exists).
    /// </summary>
    public bool UsingDefaults => _usingDefaults;

    /// <summary>
    /// Path to the mock hardware configuration file.
    /// </summary>
    public string ConfigFilePath => _configPath;

    /// <summary>
    /// Load configuration from YAML file, or use defaults if file doesn't exist.
    /// </summary>
    public void Load()
    {
        _logger.LogDebug("Loading mock hardware configuration from {ConfigPath}", _configPath);

        string? yaml = null;
        bool fileExists;

        try
        {
            fileExists = File.Exists(_configPath);
            if (fileExists)
            {
                yaml = File.ReadAllText(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read mock hardware configuration from {ConfigPath}", _configPath);
            _lock.EnterWriteLock();
            try
            {
                _config = CreateDefaultConfiguration();
                _usingDefaults = true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            if (!fileExists || string.IsNullOrWhiteSpace(yaml))
            {
                _logger.LogInformation(
                    "Mock hardware config file not found at {ConfigPath}, using hardcoded defaults",
                    _configPath);
                _config = CreateDefaultConfiguration();
                _usingDefaults = true;
                return;
            }

            // File exists - parse it and use its contents (complete override)
            var parsed = _deserializer.Deserialize<MockHardwareConfiguration>(yaml);
            if (parsed != null)
            {
                _config = parsed;
                _usingDefaults = false;
                _logger.LogInformation(
                    "Loaded mock hardware config: {DeviceCount} audio devices, {CardCount} cards, {BoardCount} relay boards",
                    _config.AudioDevices.Count,
                    _config.AudioCards.Count,
                    _config.RelayBoards.Count);
            }
            else
            {
                _logger.LogWarning("Mock hardware config file was empty or invalid, using defaults");
                _config = CreateDefaultConfiguration();
                _usingDefaults = true;
            }
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            _logger.LogError(ex,
                "Failed to parse mock hardware YAML from {ConfigPath}. Using defaults",
                _configPath);
            _config = CreateDefaultConfiguration();
            _usingDefaults = true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Save current configuration to YAML file.
    /// </summary>
    public bool Save()
    {
        string yaml;
        _lock.EnterReadLock();
        try
        {
            yaml = _serializer.Serialize(_config);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        try
        {
            File.WriteAllText(_configPath, yaml);
            _usingDefaults = false;
            _logger.LogInformation("Mock hardware configuration saved to {ConfigPath}", _configPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save mock hardware configuration to {ConfigPath}", _configPath);
            return false;
        }
    }

    /// <summary>
    /// Get enabled audio devices only.
    /// </summary>
    public IReadOnlyList<MockAudioDeviceConfig> GetEnabledAudioDevices()
    {
        _lock.EnterReadLock();
        try
        {
            return _config.AudioDevices.Where(d => d.Enabled).ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get enabled audio cards only.
    /// </summary>
    public IReadOnlyList<MockAudioCardConfig> GetEnabledAudioCards()
    {
        _lock.EnterReadLock();
        try
        {
            return _config.AudioCards.Where(c => c.Enabled).ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get enabled relay boards only.
    /// </summary>
    public IReadOnlyList<MockRelayBoardConfig> GetEnabledRelayBoards()
    {
        _lock.EnterReadLock();
        try
        {
            return _config.RelayBoards.Where(b => b.Enabled).ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Set the enabled state of an audio device.
    /// </summary>
    public bool SetAudioDeviceEnabled(string deviceId, bool enabled)
    {
        _lock.EnterWriteLock();
        try
        {
            var device = _config.AudioDevices.FirstOrDefault(d =>
                d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                _logger.LogWarning("Audio device '{DeviceId}' not found in mock config", deviceId);
                return false;
            }

            device.Enabled = enabled;
            _logger.LogInformation("Mock audio device '{DeviceId}' enabled={Enabled}", deviceId, enabled);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Set the enabled state of a relay board.
    /// </summary>
    public bool SetRelayBoardEnabled(string boardId, bool enabled)
    {
        _lock.EnterWriteLock();
        try
        {
            var board = _config.RelayBoards.FirstOrDefault(b =>
                b.BoardId.Equals(boardId, StringComparison.OrdinalIgnoreCase));

            if (board == null)
            {
                _logger.LogWarning("Relay board '{BoardId}' not found in mock config", boardId);
                return false;
            }

            board.Enabled = enabled;
            _logger.LogInformation("Mock relay board '{BoardId}' enabled={Enabled}", boardId, enabled);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Reset configuration to hardcoded defaults.
    /// Does not delete the config file - call Save() after to persist.
    /// </summary>
    public void Reset()
    {
        _lock.EnterWriteLock();
        try
        {
            _config = CreateDefaultConfiguration();
            _usingDefaults = true;
            _logger.LogInformation("Mock hardware configuration reset to defaults");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Creates the default mock hardware configuration.
    /// These defaults match the original hardcoded mock devices.
    /// </summary>
    private static MockHardwareConfiguration CreateDefaultConfiguration()
    {
        return new MockHardwareConfiguration
        {
            AudioDevices = CreateDefaultAudioDevices(),
            AudioCards = CreateDefaultAudioCards(),
            RelayBoards = CreateDefaultRelayBoards()
        };
    }

    /// <summary>
    /// Default audio devices matching MockAudioBackend.MockDeviceConfigs.
    /// </summary>
    private static List<MockAudioDeviceConfig> CreateDefaultAudioDevices()
    {
        return new List<MockAudioDeviceConfig>
        {
            // Intel HDA onboard audio (PCI device)
            new()
            {
                Id = "alsa_output.pci-0000_00_1f.3.analog-stereo",
                Name = "Built-in Audio Analog Stereo",
                Description = "Built-in Audio",
                VendorId = "8086",
                ProductId = "a170",
                BusPath = "/devices/pci0000:00/0000:00:1f.3/sound/card0",
                Index = 0,
                CardIndex = 0,
                IsDefault = true,
                MaxChannels = 2
            },
            // ASUS Xonar DX 7.1 (PCI device)
            new()
            {
                Id = "alsa_output.pci-0000_05_04.0.analog-surround-71",
                Name = "Xonar DX Analog Surround 7.1",
                Description = "CMI8788 [Oxygen HD Audio]",
                VendorId = "1043",
                ProductId = "8275",
                BusPath = "/devices/pci0000:00/0000:00:1c.4/0000:05:04.0/sound/card1",
                Index = 1,
                CardIndex = 1,
                MaxChannels = 8
            },
            // Schiit Modi 3 USB DAC
            new()
            {
                Id = "alsa_output.usb-Schiit_Audio_Schiit_Modi_3-00.analog-stereo",
                Name = "Schiit Modi 3 Analog Stereo",
                Description = "Schiit Modi 3",
                VendorId = "30be",
                ProductId = "0101",
                BusPath = "/devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2:1.0/sound/card2",
                Serial = "0001",
                Index = 2,
                CardIndex = 2,
                MaxChannels = 2
            },
            // Focusrite Scarlett 2i2 USB audio interface
            new()
            {
                Id = "alsa_output.usb-Focusrite_Scarlett_2i2_USB-00.analog-stereo",
                Name = "Scarlett 2i2 USB Analog Stereo",
                Description = "Scarlett 2i2 USB",
                VendorId = "1235",
                ProductId = "8210",
                BusPath = "/devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/sound/card3",
                Serial = "Y7XXXXXX00XXXX",
                Index = 3,
                CardIndex = 3,
                MaxChannels = 2
            },
            // JBL Flip 5 Bluetooth speaker
            new()
            {
                Id = "bluez_sink.00_1A_7D_DA_71_13.a2dp_sink",
                Name = "JBL Flip 5",
                Description = "JBL Flip 5",
                Serial = "00:1A:7D:DA:71:13",
                Index = 4,
                CardIndex = 4,
                MaxChannels = 2
            },
            // Sony WH-1000XM4 Bluetooth headphones
            new()
            {
                Id = "bluez_sink.38_18_4C_E9_85_B2.a2dp_sink",
                Name = "WH-1000XM4",
                Description = "WH-1000XM4",
                Serial = "38:18:4C:E9:85:B2",
                Index = 5,
                CardIndex = 5,
                MaxChannels = 2
            },
            // NVIDIA HDMI audio (PCI device - GPU)
            new()
            {
                Id = "alsa_output.pci-0000_01_00.1.hdmi-stereo",
                Name = "HDA NVidia Digital Stereo (HDMI)",
                Description = "HDA NVidia",
                VendorId = "10de",
                ProductId = "0fb9",
                BusPath = "/devices/pci0000:00/0000:00:01.0/0000:01:00.1/sound/card6",
                Index = 6,
                CardIndex = 6,
                MaxChannels = 2
            }
        };
    }

    /// <summary>
    /// Default audio cards matching MockCardEnumerator.MockCardConfigs.
    /// </summary>
    private static List<MockAudioCardConfig> CreateDefaultAudioCards()
    {
        return new List<MockAudioCardConfig>
        {
            // Intel HDA onboard audio
            new()
            {
                Name = "alsa_card.pci-0000_00_1f.3",
                Description = "Built-in Audio",
                Index = 0,
                Profiles = new List<MockCardProfileConfig>
                {
                    new() { Name = "output:analog-stereo+input:analog-stereo", Description = "Analog Stereo Duplex", Sinks = 1, Priority = 6565, IsDefault = true },
                    new() { Name = "output:analog-stereo", Description = "Analog Stereo Output", Sinks = 1, Priority = 6500 },
                    new() { Name = "output:hdmi-stereo", Description = "Digital Stereo (HDMI)", Sinks = 1, Priority = 5900 },
                    new() { Name = "output:hdmi-stereo-extra1", Description = "Digital Stereo (HDMI 2)", Sinks = 1, Priority = 5700 },
                    new() { Name = "off", Description = "Off", Sinks = 0, Priority = 0 }
                }
            },
            // ASUS Xonar DX
            new()
            {
                Name = "alsa_card.pci-0000_05_04.0",
                Description = "Xonar DX",
                Index = 1,
                Profiles = new List<MockCardProfileConfig>
                {
                    new() { Name = "output:analog-surround-71+input:analog-stereo", Description = "Analog Surround 7.1 Output + Analog Stereo Input", Sinks = 1, Priority = 7171, IsDefault = true },
                    new() { Name = "output:analog-surround-71", Description = "Analog Surround 7.1 Output", Sinks = 1, Priority = 7100 },
                    new() { Name = "output:analog-surround-51", Description = "Analog Surround 5.1 Output", Sinks = 1, Priority = 6100 },
                    new() { Name = "output:analog-stereo+input:analog-stereo", Description = "Analog Stereo Duplex", Sinks = 1, Priority = 6065 },
                    new() { Name = "output:analog-stereo", Description = "Analog Stereo Output", Sinks = 1, Priority = 6000 },
                    new() { Name = "output:iec958-stereo+input:analog-stereo", Description = "Digital Stereo (S/PDIF) Output + Analog Stereo Input", Sinks = 1, Priority = 5565 },
                    new() { Name = "output:iec958-stereo", Description = "Digital Stereo (S/PDIF) Output", Sinks = 1, Priority = 5500 },
                    new() { Name = "off", Description = "Off", Sinks = 0, Priority = 0 }
                }
            },
            // Schiit Modi 3
            new()
            {
                Name = "alsa_card.usb-Schiit_Audio_Schiit_Modi_3-00",
                Description = "Schiit Modi 3",
                Index = 2,
                Profiles = new List<MockCardProfileConfig>
                {
                    new() { Name = "output:analog-stereo", Description = "Analog Stereo Output", Sinks = 1, Priority = 6500, IsDefault = true },
                    new() { Name = "output:iec958-stereo", Description = "Digital Stereo (IEC958) Output", Sinks = 1, Priority = 5500 },
                    new() { Name = "off", Description = "Off", Sinks = 0, Priority = 0 }
                }
            },
            // Focusrite Scarlett 2i2
            new()
            {
                Name = "alsa_card.usb-Focusrite_Scarlett_2i2_USB-00",
                Description = "Scarlett 2i2 USB",
                Index = 3,
                Profiles = new List<MockCardProfileConfig>
                {
                    new() { Name = "output:analog-surround-71", Description = "Analog Surround 7.1 Output", Sinks = 1, Priority = 7100, IsDefault = true },
                    new() { Name = "output:analog-surround-51", Description = "Analog Surround 5.1 Output", Sinks = 1, Priority = 6100 },
                    new() { Name = "output:analog-stereo", Description = "Analog Stereo Output", Sinks = 1, Priority = 6000 },
                    new() { Name = "off", Description = "Off", Sinks = 0, Priority = 0 }
                }
            },
            // JBL Flip 5 Bluetooth
            new()
            {
                Name = "bluez_card.00_1A_7D_DA_71_13",
                Description = "JBL Flip 5",
                Driver = "module-bluez5-device.c",
                Index = 4,
                Profiles = new List<MockCardProfileConfig>
                {
                    new() { Name = "a2dp-sink", Description = "High Fidelity Playback (A2DP Sink)", Sinks = 1, Priority = 40, IsDefault = true },
                    new() { Name = "headset-head-unit", Description = "Headset Head Unit (HSP/HFP)", Sinks = 1, Priority = 30 },
                    new() { Name = "off", Description = "Off", Sinks = 0, Priority = 0 }
                }
            },
            // Sony WH-1000XM4 Bluetooth
            new()
            {
                Name = "bluez_card.38_18_4C_E9_85_B2",
                Description = "WH-1000XM4",
                Driver = "module-bluez5-device.c",
                Index = 5,
                Profiles = new List<MockCardProfileConfig>
                {
                    new() { Name = "a2dp-sink", Description = "High Fidelity Playback (A2DP Sink)", Sinks = 1, Priority = 40, IsDefault = true },
                    new() { Name = "headset-head-unit", Description = "Headset Head Unit (HSP/HFP)", Sinks = 1, Priority = 30 },
                    new() { Name = "off", Description = "Off", Sinks = 0, Priority = 0 }
                }
            },
            // NVIDIA HDMI
            new()
            {
                Name = "alsa_card.pci-0000_01_00.1",
                Description = "HDA NVidia",
                Index = 6,
                Profiles = new List<MockCardProfileConfig>
                {
                    new() { Name = "output:hdmi-stereo", Description = "Digital Stereo (HDMI)", Sinks = 1, Priority = 5900, IsDefault = true },
                    new() { Name = "output:hdmi-surround", Description = "Digital Surround 5.1 (HDMI)", Sinks = 1, Priority = 5800 },
                    new() { Name = "output:hdmi-surround71", Description = "Digital Surround 7.1 (HDMI)", Sinks = 1, Priority = 5700 },
                    new() { Name = "off", Description = "Off", Sinks = 0, Priority = 0 }
                }
            }
        };
    }

    /// <summary>
    /// Default relay boards matching MockRelayDeviceEnumerator.
    /// </summary>
    private static List<MockRelayBoardConfig> CreateDefaultRelayBoards()
    {
        return new List<MockRelayBoardConfig>
        {
            new()
            {
                BoardId = "MOCK001",
                BoardType = "ftdi",
                SerialNumber = "MOCK001",
                Description = "Mock 8-Channel FTDI Relay Board",
                ChannelCount = 8
            },
            new()
            {
                BoardId = "MOCK002",
                BoardType = "ftdi",
                SerialNumber = "MOCK002",
                Description = "Mock 8-Channel FTDI Relay Board",
                ChannelCount = 8
            },
            new()
            {
                BoardId = "HID:QAAMZ",
                BoardType = "usb_hid",
                SerialNumber = "QAAMZ",
                Description = "USBRelay4 - 4 Channel USB HID Relay",
                ChannelCount = 4,
                ChannelCountDetected = true
            },
            new()
            {
                BoardId = "HID:MOCK8",
                BoardType = "usb_hid",
                SerialNumber = "MOCK8",
                Description = "Generic 8-Channel USB HID Relay",
                ChannelCount = 8,
                ChannelCountDetected = true
            },
            new()
            {
                BoardId = "MODBUS:/dev/ttyUSB0",
                BoardType = "modbus",
                Description = "Sainsmart 16-Channel Modbus Relay",
                ChannelCount = 16,
                ChannelCountDetected = false,
                UsbPath = "/dev/ttyUSB0"
            }
        };
    }
}
