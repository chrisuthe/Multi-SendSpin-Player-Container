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
- **Docker note:** Requires both `/dev/bus/usb` (for device discovery) AND `/dev/hidraw*` (for device control)—see [Docker Setup](#docker-setup)

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

USB HID relay boards require **two** device mappings to work correctly:

1. **`/dev/bus/usb`** — Required for device discovery (finding connected boards)
2. **`/dev/hidraw*`** — Required for device control (actually operating the relays)

```yaml
services:
  multiroom-audio:
    devices:
      - /dev/bus/usb:/dev/bus/usb    # Required: device discovery
      - /dev/hidraw0:/dev/hidraw0    # Required: relay control
      - /dev/hidraw1:/dev/hidraw1    # Add one line per HID relay board
```

> **Why both?** The USB bus lets us enumerate and identify relay boards by their USB port location. But Linux exposes HID device control through the `/dev/hidraw*` interface, not through `/dev/bus/usb`. Without the hidraw mapping, boards appear in the UI but can't actually be controlled.

#### Finding Your hidraw Devices

```bash
# List all hidraw devices
ls -la /dev/hidraw*

# Find which hidraw corresponds to your relay board (VID 16c0, PID 05df)
for dev in /dev/hidraw*; do
  info=$(udevadm info -a "$dev" 2>/dev/null | grep -E 'ATTRS\{idVendor\}|ATTRS\{idProduct\}' | head -2)
  if echo "$info" | grep -q '16c0' && echo "$info" | grep -q '05df'; then
    echo "Relay board found at: $dev"
  fi
done
```

#### Hidraw Numbers Can Change After Reboot

The `/dev/hidraw*` numbers are assigned dynamically based on device enumeration order. They typically remain stable across reboots **as long as the same HID devices are present**. However, if you add or remove any HID device (keyboard, mouse, another relay board, etc.), the numbers may shift.

When this happens:

1. The board will show as "Disconnected" in the UI with an error message
2. **Check the application logs** — they will tell you exactly which hidraw device the board now uses
3. Update your `compose.yml` with the new device mapping
4. Restart the container

Example log message when a board is found but can't be opened:

```text
HID relay board found but cannot open: USBRelay4 at /sys/.../hidraw/hidraw2.
Add '- /dev/hidraw2:/dev/hidraw2' to your compose.yml devices section.
```

> **Important:** Always keep the same device number on both sides of the mapping (e.g., `/dev/hidraw2:/dev/hidraw2`, not `/dev/hidraw2:/dev/hidraw0`). The internal board identification uses a hash of the USB port path, not the hidraw number, so changing physical USB ports will require reconfiguring the board in the UI.

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

#### Multiple Modbus Boards

If you have multiple CH340-based boards, use `/dev/serial/by-path/` for stable identification. The `/dev/ttyUSB*` names can swap between reboots.

```bash
# Find stable paths for your devices
ls -la /dev/serial/by-path/
```

Example output:

```text
pci-0000:00:14.0-usb-0:2.1:1.0-port0 -> ../../ttyUSB0
pci-0000:00:14.0-usb-0:2.2:1.0-port0 -> ../../ttyUSB1
```

Use these paths in your compose file:

```yaml
services:
  multiroom-audio:
    devices:
      - /dev/serial/by-path/pci-0000:00:14.0-usb-0:2.1:1.0-port0:/dev/ttyUSB0
      - /dev/serial/by-path/pci-0000:00:14.0-usb-0:2.2:1.0-port0:/dev/ttyUSB1
```

This ensures each physical USB port always maps to the same `/dev/ttyUSB*` device inside the container.

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

Boards are identified using stable identifiers that persist across reboots:

| Board Type | ID Format | Example | Stability |
| ---------- | --------- | ------- | --------- |
| **USB HID** | `HID:<hash>` | `HID:CA88BCAC` | Stable if board stays in same USB port |
| **FTDI** | Serial number | `A12345` | Stable regardless of USB port |
| **Modbus** | `MODBUS:<hash>` | `MODBUS:7F3A2B1C` | Stable if board stays in same USB port |

### How HID Board Identification Works

USB HID relay boards often lack unique serial numbers—many boards from the same batch have identical or garbage serial data. To work around this, we identify HID boards by computing a hash of their **USB port path** (e.g., `usb1/1-3`).

This means:

- **Same USB port = same board ID** — You can unplug and replug the board and it will reconnect automatically
- **Different USB port = different board ID** — Moving a board to a different USB port will make it appear as a new board in the UI
- **Hidraw number doesn't matter** — The hidraw number (e.g., `/dev/hidraw2`) can change without affecting board identification; you just need to update the Docker device mapping

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

### HID Board Shows "Disconnected" (Docker)

If your HID relay board appears in the UI but shows as disconnected with an error, this usually means the hidraw device mapping is missing or outdated.

**Check the logs first** — The application will tell you exactly what to do:

```text
HID relay board found but cannot open: USBRelay4 at /sys/.../hidraw/hidraw2.
Add '- /dev/hidraw2:/dev/hidraw2' to your compose.yml devices section.
```

**Common causes:**

1. **Missing hidraw mapping** — You have `/dev/bus/usb` but forgot the `/dev/hidraw*` mapping
2. **Hidraw number changed** — A HID device was added/removed since you configured the container
3. **Wrong hidraw number** — The mapping exists but points to the wrong device

**Solution:**

1. Check the logs to find the correct hidraw device
2. Update your `compose.yml` with the correct mapping
3. Restart the container

### Relay Not Responding

1. **Test manually** using the Test button in the UI
2. **Check permissions** — ensure container has access to device
3. **Review logs** for error messages
4. **Verify wiring** — relay boards typically need separate power supply

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
