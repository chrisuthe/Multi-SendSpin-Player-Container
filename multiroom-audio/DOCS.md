# Multi-Room Audio Controller

<!-- VERSION_INFO_START -->
## Latest Release: 2.0.12

Audio Quality and PulseAudio Fixes

- Fixed audio artifacts from sync correction (clicks/pops)
- Fixed resampler transition pops
- Fixed PulseAudio timing jitter with wider deadband
- Dynamic ALSA enumeration for better Docker compatibility

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

### Example Configuration

```yaml
log_level: info
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

## Known Limitations

1. **Sendspin only**: This add-on only supports Music Assistant via Sendspin protocol
2. **PulseAudio on HAOS**: Device names differ from standalone Docker deployments
3. **Permissions**: Requires `full_access` for proper audio device access

## Support

- [GitHub Issues](https://github.com/chrisuthe/squeezelite-docker/issues)
- [Documentation](https://github.com/chrisuthe/squeezelite-docker)

## About

This add-on was created using AI-assisted development (Claude by Anthropic).
See the project README for more details.

## Credits

- Icon: [Music note icons created by Freepik - Flaticon](https://www.flaticon.com/free-icons/music-note)
