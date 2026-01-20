# 12V Trigger Control

Automatically power on/off amplifiers when audio playback starts and stops using USB relay boards.

## Overview

12V triggers are commonly used to signal amplifiers to power on or enter standby. This add-on can control USB relay boards to automate this process—when a player starts streaming, the assigned relay closes (12V on), and when playback stops, the relay opens after a configurable delay.

## Supported Hardware

> **Note:** Only the specific boards listed below have been tested. Other variants from these manufacturers may or may not work.

| Type | VID:PID | Tested Products | Channel Detection |
|------|---------|-----------------|-------------------|
| **USB HID** | `0x16C0:0x05DF` | [ELEGOO 4-Channel USB Relay](https://www.amazon.com/dp/B07C3MQPB1) | Auto-detected from product name |
| **FTDI** | `0x0403:0x6001` | [Denkovi DAE0006K 8-Channel](https://denkovi.com/usb-8-relay-board-dae0006k) | Manual configuration |
| **Modbus/CH340** | `0x1A86:0x7523` | [Sainsmart 16-Channel](https://www.amazon.com/dp/B0793MZH2B) | Manual configuration |

### USB HID Relay Boards

- Most common and easiest to set up
- 1, 2, 4, or 8 channel variants available
- Channel count auto-detected from device name (e.g., "USBRelay4")
- No drivers required on Linux

### FTDI Relay Boards

- Uses FT245RL chip in bitbang mode
- Requires `libftdi1` library
- May need `SYS_RAWIO` capability if kernel driver claims device

### Modbus/CH340 Relay Boards

- Serial-based using Modbus ASCII protocol
- Appears as `/dev/ttyUSB*` on Linux
- 4, 8, or 16 channel variants available
- Channel count must be configured manually

## Docker Setup

### USB HID Boards

```yaml
services:
  multiroom-audio:
    devices:
      - /dev/bus/usb:/dev/bus/usb
```

Or pass through specific hidraw device:

```yaml
    devices:
      - /dev/hidraw0:/dev/hidraw0
```

### FTDI Boards

```yaml
services:
  multiroom-audio:
    devices:
      - /dev/bus/usb:/dev/bus/usb
    cap_add:
      - SYS_RAWIO  # Only if ftdi_sio kernel driver claims device
```

### Modbus/CH340 Boards

```yaml
services:
  multiroom-audio:
    devices:
      - /dev/ttyUSB0:/dev/ttyUSB0
```

Find your serial port:

```bash
dmesg | grep ttyUSB
# or
ls /dev/ttyUSB*
```

## Home Assistant OS Setup

USB relay boards should work automatically when connected. If not detected:

1. Verify the device appears in Home Assistant's hardware settings
2. Restart the add-on after connecting the board
3. For Modbus boards, ensure the serial port is visible in hardware settings

### Optional: Explicit Device Configuration

For more granular control, you can configure specific devices in the add-on Configuration tab:

| Option | Description |
|--------|-------------|
| **Relay Serial Port** | Dropdown to select a serial port for Modbus/CH340 boards |
| **Relay Devices** | List of additional device paths (e.g., `/dev/hidraw0` for HID boards) |

Example YAML configuration:

```yaml
log_level: info
relay_serial_port: /dev/ttyUSB0
relay_devices:
  - /dev/hidraw0
```

This is optional—by default, the add-on has access to all USB and serial devices.

## Configuration

### Enable Triggers

1. Open **Settings** > **12V Triggers** in the web interface
2. Toggle **Enable 12V Triggers**

### Add a Relay Board

1. Click **Add Relay Board**
2. Select your detected board from the dropdown
3. For boards without auto-detection, configure the channel count

### Assign Channels

Each relay channel can be assigned to a custom sink (audio device):

1. Expand the board configuration
2. For each channel, select the sink that should trigger it
3. Set an optional **Zone Name** (e.g., "Living Room Amp")
4. Configure **Off Delay** (seconds to wait before turning relay off)

### Startup/Shutdown Behavior

Each board can be configured with power-on and power-off behavior:

| Behavior | Description |
|----------|-------------|
| **All Off** (default) | Turn all relays OFF—safest, amps start powered down |
| **All On** | Turn all relays ON—useful if amps should always be powered |
| **No Change** | Preserve current relay state |

## Board Identification

Boards are identified by:

1. **Serial Number** (preferred)—stable across reboots and USB port changes
2. **USB Port Path** (fallback)—format: `USB:1-2.3`
3. **Serial Port** (Modbus)—format: `MODBUS:/dev/ttyUSB0`

## Troubleshooting

### Board Not Detected

**USB HID:**
```bash
# Check if device is connected
lsusb | grep 16c0
# Should show: 16c0:05df Van Ooijen Technische Informatica

# Check hidraw devices
ls -la /dev/hidraw*
```

**FTDI:**
```bash
lsusb | grep 0403
# Should show: 0403:6001 Future Technology Devices International
```

**Modbus/CH340:**
```bash
lsusb | grep 1a86
# Should show: 1a86:7523 QinHeng Electronics CH340 serial converter

dmesg | grep ttyUSB
# Should show: ch341-uart converter now attached to ttyUSB0
```

### Relay Not Responding

1. **Test manually** using the Test button in the UI
2. **Check permissions**—ensure container has access to device
3. **Review logs** for error messages
4. **Verify wiring**—relay boards typically need separate power supply

### Multiple Boards with Same Serial

Some boards (especially HID) have identical or no serial numbers. Use USB port path for identification:

1. Note which physical USB port the board is connected to
2. The path (e.g., `USB:1-2.3`) remains stable if you don't move the board

## API Reference

See [CLAUDE.md](../CLAUDE.md) for full API documentation. Key endpoints:

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/triggers` | Get status and all boards |
| PUT | `/api/triggers/enabled` | Enable/disable feature |
| GET | `/api/triggers/devices/all` | List detected relay devices |
| POST | `/api/triggers/boards` | Add a relay board |
| POST | `/api/triggers/boards/{boardId}/{channel}/test` | Test relay |

## Testing Without Hardware

Set environment variable `MOCK_HARDWARE=true` to enable simulated relay boards for development and testing. See [MOCK_HARDWARE.md](MOCK_HARDWARE.md) for configuration options.
