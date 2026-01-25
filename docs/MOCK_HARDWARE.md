# Mock Hardware Configuration

When running with `MOCK_HARDWARE=true`, you can customize the simulated hardware by creating a `mock_hardware.yaml` file in your config directory.

## Quick Start

1. Set `MOCK_HARDWARE=true` environment variable
2. Create `config/mock_hardware.yaml` (optional - defaults are provided)
3. Restart the application

If no config file exists, default mock devices are used automatically.

## Configuration File Location

| Environment | Path |
|-------------|------|
| Docker | `/app/config/mock_hardware.yaml` |
| HAOS Add-on | `/data/mock_hardware.yaml` |
| Development | `./config/mock_hardware.yaml` or `./test-config/config/mock_hardware.yaml` |

## Configuration Behavior

**When `mock_hardware.yaml` does NOT exist:**
- Hardcoded defaults are used (7 audio devices, 7 cards, 4 relay boards)
- No config file needed for standard mock testing

**When `mock_hardware.yaml` exists:**
- YAML completely replaces all defaults
- Only devices defined in the YAML file will be available
- Hardcoded defaults are ignored entirely

This "complete override" approach provides:
- Simple mental model: what's in the file is what you get
- Full control: easy to test with exactly 2 devices, or 20
- No merge confusion: no wondering "where did device X come from?"

## YAML Schema

### Audio Devices

Simulates PulseAudio sinks (audio outputs).

```yaml
audio_devices:
  - id: string              # Required. PulseAudio sink name (e.g., "alsa_output.usb-...")
    enabled: boolean        # Optional. Default: true. Set false to "disconnect" device
    name: string            # Required. Display name (e.g., "Living Room DAC")
    description: string     # Optional. Device description
    vendor_id: string       # Optional. USB vendor ID (e.g., "30be")
    product_id: string      # Optional. USB product ID (e.g., "0101")
    bus_path: string        # Optional. Sysfs bus path for device matching
    serial: string          # Optional. Device serial number
    is_default: boolean     # Optional. Default: false. Only one device should be default
    max_channels: integer   # Optional. Default: 2. Channel count (2=stereo, 8=7.1)
    index: integer          # Optional. PulseAudio device index
```

**ID Format Examples:**
- USB DAC: `alsa_output.usb-Vendor_Product_Name-00.analog-stereo`
- PCI sound card: `alsa_output.pci-0000_00_1f.3.analog-stereo`
- Bluetooth: `bluez_sink.AA_BB_CC_DD_EE_FF.a2dp_sink`
- HDMI: `alsa_output.pci-0000_01_00.1.hdmi-stereo`

### Audio Cards

Simulates PulseAudio sound cards with profiles.

```yaml
audio_cards:
  - name: string            # Required. Card name (e.g., "alsa_card.usb-...")
    enabled: boolean        # Optional. Default: true
    description: string     # Required. Card description
    driver: string          # Optional. Default: "module-alsa-card.c"
    index: integer          # Optional. Card index
    profiles:               # Required. List of available profiles
      - name: string        # Profile name (e.g., "output:analog-stereo")
        description: string # Profile description
        sinks: integer      # Number of sinks this profile provides (default: 1)
        priority: integer   # Profile priority (higher = preferred)
        is_available: boolean # Whether profile is available (default: true)
        is_default: boolean # Whether this is the active profile
```

**Common Profile Names:**
- `output:analog-stereo` - Stereo output
- `output:analog-surround-51` - 5.1 surround
- `output:analog-surround-71` - 7.1 surround
- `output:hdmi-stereo` - HDMI stereo
- `a2dp-sink` - Bluetooth A2DP
- `off` - Card disabled

### Relay Boards

Simulates FTDI, USB HID, and Modbus relay boards.

```yaml
relay_boards:
  - board_id: string        # Required. Unique identifier (e.g., "MOCK001", "HID:SERIAL", or "MODBUS:/dev/ttyUSB0")
    enabled: boolean        # Optional. Default: true
    board_type: string      # Required. "ftdi", "usb_hid", or "modbus"
    serial_number: string   # Optional. Board serial number
    description: string     # Optional. Board description
    channel_count: integer  # Required. Number of relay channels (1-16)
    channel_count_detected: boolean  # Optional. True if channels auto-detected
    usb_path: string        # Optional. USB port path (e.g., "1-2.3") or serial port (e.g., "/dev/ttyUSB0")
```

**Board ID Format:**
- FTDI: Use serial number directly (e.g., `MOCK001`)
- HID: Prefix with `HID:` (e.g., `HID:QAAMZ`)
- Modbus: Prefix with `MODBUS:` and port path (e.g., `MODBUS:/dev/ttyUSB0`)

## Complete Example

```yaml
# Mock Hardware Configuration
# Simulates a setup with 3 USB DACs and 2 relay boards

audio_devices:
  # Main stereo DAC - always connected
  - id: alsa_output.usb-Schiit_Audio_Modi_3-00.analog-stereo
    enabled: true
    name: "Living Room DAC"
    description: "Schiit Modi 3"
    vendor_id: "30be"
    product_id: "0101"
    serial: "0001"
    is_default: true
    max_channels: 2
    index: 0

  # Secondary DAC - simulating disconnected
  - id: alsa_output.usb-Topping_D10-00.analog-stereo
    enabled: false  # Unplugged
    name: "Kitchen DAC"
    description: "Topping D10"
    vendor_id: "152a"
    product_id: "8750"
    max_channels: 2
    index: 1

  # Bluetooth speaker
  - id: bluez_sink.00_1A_7D_DA_71_13.a2dp_sink
    enabled: true
    name: "Patio Speaker"
    description: "JBL Flip 5"
    serial: "00:1A:7D:DA:71:13"
    max_channels: 2
    index: 2

audio_cards:
  - name: alsa_card.usb-Schiit_Audio_Modi_3-00
    enabled: true
    description: "Schiit Modi 3"
    index: 0
    profiles:
      - name: "output:analog-stereo"
        description: "Analog Stereo Output"
        sinks: 1
        priority: 6500
        is_available: true
        is_default: true
      - name: "off"
        description: "Off"
        sinks: 0
        priority: 0
        is_available: true

  - name: bluez_card.00_1A_7D_DA_71_13
    enabled: true
    description: "JBL Flip 5"
    driver: "module-bluez5-device.c"
    index: 2
    profiles:
      - name: "a2dp-sink"
        description: "High Fidelity Playback (A2DP Sink)"
        sinks: 1
        priority: 40
        is_default: true
      - name: "off"
        description: "Off"
        sinks: 0

relay_boards:
  # 8-channel FTDI board for amp triggers
  - board_id: "FTDI001"
    enabled: true
    board_type: ftdi
    serial_number: "FTDI001"
    description: "Main Amp Triggers"
    channel_count: 8

  # 4-channel HID board for zone control
  - board_id: "HID:RELAY4"
    enabled: true
    board_type: usb_hid
    serial_number: "RELAY4"
    description: "Zone Relays"
    channel_count: 4
    channel_count_detected: true

  # 16-channel Modbus relay board (Sainsmart style)
  - board_id: "MODBUS:/dev/ttyUSB0"
    enabled: true
    board_type: modbus
    description: "Sainsmart 16-Channel Relay"
    channel_count: 16
    usb_path: "/dev/ttyUSB0"
```

## Default Devices

When no `mock_hardware.yaml` exists, the following defaults are used:

### Audio Devices (7)

| Device | Type | Channels | Default |
|--------|------|----------|---------|
| Built-in Audio Analog Stereo | Intel HDA | 2 | Yes |
| Xonar DX Analog Surround 7.1 | ASUS Xonar | 8 | No |
| Schiit Modi 3 Analog Stereo | USB DAC | 2 | No |
| Scarlett 2i2 USB Analog Stereo | Focusrite | 2 | No |
| JBL Flip 5 | Bluetooth | 2 | No |
| WH-1000XM4 | Bluetooth | 2 | No |
| HDA NVidia Digital Stereo (HDMI) | HDMI | 2 | No |

### Relay Boards (5)

| Board ID | Type | Channels | Description |
|----------|------|----------|-------------|
| MOCK001 | FTDI | 8 | Mock 8-Channel FTDI Relay Board |
| MOCK002 | FTDI | 8 | Mock 8-Channel FTDI Relay Board |
| HID:QAAMZ | USB HID | 4 | USBRelay4 - 4 Channel USB HID Relay |
| HID:MOCK8 | USB HID | 8 | Generic 8-Channel USB HID Relay |
| MODBUS:/dev/ttyUSB0 | Modbus | 16 | Sainsmart 16-Channel Modbus Relay |

## Tips

1. **Simulate disconnection**: Set `enabled: false` on any device
2. **Test edge cases**: Create devices with unusual channel counts or missing serials
3. **Match real hardware**: Copy vendor/product IDs from your actual devices
4. **Bluetooth testing**: Use `bluez_sink.*` IDs with MAC address format serial
5. **Multiple defaults**: Only one audio device should have `is_default: true`
6. **Card-Device pairing**: For full functionality, include matching audio cards for your audio devices
7. **Profile testing**: Add multiple profiles to cards to test profile switching
