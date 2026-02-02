# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-0e83da6

**Features in Development** (targeting 5.0 release)

- **12V Trigger Relay Control** - Automatic amplifier power management via USB HID, FTDI, and Modbus relay boards
- **Player Mute Button** - Bidirectional mute sync with Music Assistant
- **Now Playing Info** - Track title, artist, album in Player Details modal
- **Device Capabilities** - Shows supported sample rates, bit depths, channels
- **Volume Persistence** - Volume survives container restarts
- **Reconnection UX** - Startup progress overlay, WaitingForServer state, auto-reconnect
- **Sync Improvements** - Anti-oscillation debounce, latency lock-in
- **Mono Output** - Remap sinks support single-channel output
- **International Names** - Unicode player names (emojis, CJK, etc.)
- **SendSpin.SDK 6.1.1** - Major protocol improvements

> WARNING: This is a development build. For stable releases, use the stable add-on.
<!-- VERSION_INFO_END -->

---

## Warning

Development builds:
- May contain bugs or incomplete features
- Could have breaking changes between builds
- Are not recommended for production use

## Installation

This add-on is automatically updated whenever code is pushed to the `dev` branch.
The version number (sha-XXXXXXX) indicates the commit it was built from.

## Reporting Issues

When reporting issues with dev builds, please include:
- The commit SHA (visible in the add-on info)
- Steps to reproduce the issue
- Expected vs actual behavior

## Configuration

### Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `log_level` | string | `info` | Logging verbosity (debug, info, warning, error) |
| `relay_serial_port` | device | null | Serial port for Modbus/CH340 relay board |
| `relay_devices` | list | `[]` | Device paths for HID/FTDI relay boards |
| `mock_hardware` | bool | `false` | Enable mock relay boards for testing without hardware |
| `enable_advanced_formats` | bool | `false` | Show format selection UI (players default to flac-48000 regardless) |

## For Stable Release

Use the "Multi-Room Audio Controller" add-on (without "Dev") for stable releases.
