# Multi-Room Audio Controller

<!-- VERSION_INFO_START -->
## Latest Release: 3.0.0




[View full changelog](https://github.com/chrisuthe/Multi-SendSpin-Player-Container/blob/main/multiroom-audio/CHANGELOG.md)
<!-- VERSION_INFO_END -->

---

Manage Sendspin audio players for Music Assistant. Create whole-home audio with
USB DACs connected to your Home Assistant server.

## Overview

This add-on creates Sendspin players that appear in Music Assistant as available
audio endpoints. Each player outputs to a different audio device, enabling
multi-room audio from a single server.

## How It Works

1. Connect USB audio devices (DACs) to your Home Assistant server
2. Create a player for each device in this add-on
3. Players automatically register with Music Assistant via Sendspin protocol
4. Control playback from Music Assistant's interface

## Installation

1. Add this repository to your Home Assistant add-on store
2. Install the "Multi-Room Audio Controller" add-on
3. Start the add-on
4. Access the web interface via the sidebar or ingress

## Configuration

### Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `log_level` | string | `info` | Logging verbosity (debug, info, warning, error) |
| `relay_serial_port` | device | null | Serial port for Modbus/CH340 relay board (dropdown) |
| `relay_devices` | list | `[]` | Additional device paths for HID/FTDI relay boards |

### Example Configuration

```yaml
log_level: info
```

### Example with Relay Boards

```yaml
log_level: info
relay_serial_port: /dev/ttyUSB0
relay_devices:
  - /dev/hidraw0
```

## Audio Device Setup

### Accessing USB DACs

USB audio devices connected to your Home Assistant server are automatically
detected via PortAudio. The add-on uses Home Assistant's audio system
for audio output.

### Device Selection

When creating a player:

1. Open the add-on web interface
2. Click "Add Player"
3. Select your audio device from the dropdown
4. Device names appear as PortAudio device names

### Troubleshooting Audio

If devices are not appearing:

1. Check that USB devices are properly connected
2. Verify devices appear in HA's audio settings
3. Restart the add-on to refresh device detection
4. Check add-on logs for audio errors

## Usage

### Creating Players

1. Access the web interface (via HA sidebar or ingress)
2. Click "Add Player"
3. Configure:
   - **Name**: Descriptive name (e.g., "Kitchen Speakers")
   - **Device**: Select audio output device
   - **Server IP** (optional): Music Assistant server IP for manual discovery
4. Click "Create"

### Managing Players

- **Start/Stop**: Toggle player state
- **Volume**: Adjust via slider
- **Delay Offset**: Adjust timing for multi-room sync (milliseconds)
- **Delete**: Remove player

### Integration with Music Assistant

Players automatically appear in Music Assistant within 30-60 seconds of starting.
No additional configuration is required.

If a player does not appear:
1. Verify the player is running (green status indicator)
2. Restart Music Assistant
3. Check that both add-ons are on the same network

## Network Requirements

| Port | Protocol | Direction | Purpose |
|------|----------|-----------|---------|
| 8096 | TCP | Inbound | Web interface (via ingress) |

All player communication uses mDNS for discovery and the Sendspin protocol for
streaming. No additional port configuration is required.

## 12V Trigger Control (Optional)

This add-on supports 12V trigger control via USB relay boards. This allows
automatic power-on/off of amplifiers when playback starts and stops.

### Supported Hardware

**FTDI Relay Boards:**

- Denkovi USB 8 Relay Board (DAE0006K or similar with FT245RL chip)
- Any FTDI FT245RL-based relay board (1-16 channels)

**USB HID Relay Boards:**

- DCT Tech / ucreatefun USB relay boards (1, 2, 4, or 8 channels)
- Any USB HID relay with VID 0x16C0, PID 0x05DF
- Channel count is auto-detected from product name (e.g., "USBRelay8")

**Modbus/Serial Relay Boards (CH340/CH341):**

- Sainsmart 16-channel USB relay boards
- Any CH340/CH341-based Modbus ASCII relay board (4, 8, or 16 channels)
- VID 0x1A86, PID 0x7523
- Channel count must be configured manually (cannot be auto-detected)
- Appears as `/dev/ttyUSB*` on Linux, `COM*` on Windows

### Setup (Docker)

For standalone Docker deployments, enable USB passthrough in your `docker-compose.yml`:

```yaml
services:
  multiroom-audio:
    devices:
      # Required for FTDI and USB HID relay boards
      - /dev/bus/usb:/dev/bus/usb
      # Required for CH340/Modbus relay boards (serial port)
      - /dev/ttyUSB0:/dev/ttyUSB0
    cap_add:
      # Optional: Only needed if ftdi_sio kernel driver claims your FTDI device
      # Not required for USB HID or CH340 boards
      - SYS_RAWIO
```

**Alternative: Pass through specific devices only:**

```yaml
    devices:
      # For USB HID relay boards (find your hidraw device with 'ls /dev/hidraw*')
      - /dev/hidraw0:/dev/hidraw0
      - /dev/hidraw1:/dev/hidraw1
      # For specific USB device (find path with 'lsusb')
      - /dev/bus/usb/001/002:/dev/bus/usb/001/002
      # For CH340/Modbus relay boards
      - /dev/ttyUSB0:/dev/ttyUSB0
```

> **Important:** For HID relay boards, keep the same device number on both sides of the mapping (e.g., `/dev/hidraw0:/dev/hidraw0`, not `/dev/hidraw0:/dev/hidraw3`). The hidraw numbers may change after a host reboot—check `ls /dev/hidraw*` and update your configuration if needed.

### Setup (Home Assistant OS)

USB relay boards should work automatically when connected via USB. If not
detected:
1. Ensure the USB device is visible in Home Assistant's hardware settings
2. Restart the add-on after connecting the relay board
3. For CH340/Modbus boards, check that the serial port appears in hardware settings

### Trigger Configuration

1. Open Settings > 12V Triggers in the web interface
2. Enable the trigger feature
3. Click "Add Relay Board" and select your detected board
4. Assign relay channels to custom sinks
5. Configure off-delay (time before relay turns off after playback stops)
6. Optionally set a zone name for each channel (e.g., "Living Room Amp")

### Startup/Shutdown Behavior

Each relay board can be configured with startup and shutdown behaviors:

| Behavior | Description |
| -------- | ----------- |
| **All Off** (default) | Turn all relays OFF - safest option, amplifiers start powered down |
| **All On** | Turn all relays ON - useful if you want amplifiers always powered |
| **No Change** | Preserve current relay state - hardware maintains its state |

These settings control what happens when the service starts (or board reconnects) and
when the service stops gracefully. The default "All Off" prevents amplifiers from
unexpectedly powering on after a restart.

### Multiple Boards

You can configure multiple relay boards simultaneously. Each board maintains its
own channel assignments. Boards are identified by:
- **Serial number** (preferred) - Stable across reboots and USB port changes
- **USB port path** (fallback) - For boards without unique serial numbers

## Known Limitations

1. **Sendspin only**: This add-on only supports Music Assistant via Sendspin protocol
2. **PulseAudio on HAOS**: Device names differ from standalone Docker deployments
3. **Permissions**: Requires `full_access` for proper audio device access
4. **FTDI relay boards**: Requires USB passthrough and SYS_RAWIO capability in Docker

## Support

- [GitHub Issues](https://github.com/chrisuthe/squeezelite-docker/issues)
- [Documentation](https://github.com/chrisuthe/squeezelite-docker)

## About

This add-on was created using AI-assisted development (Claude by Anthropic).
See the project README for more details.

## Credits

- Icon: [Music note icons created by Freepik - Flaticon](https://www.flaticon.com/free-icons/music-note)
