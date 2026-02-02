# Multi-Room Audio Controller

**One server. Multiple audio outputs. Whole-home audio with Music Assistant.**

> **Version 5.0 is here!** 12V trigger relay control, player mute buttons, enhanced reconnection, and more. [See what's new](WHATS_NEW_5.0).

---

## What Problem Does This Solve?

You want multi-room audio but:

- **Commercial solutions are expensive** - Sonos, HEOS cost $200-500 per room
- **You already have speakers, amps, or DACs** sitting unused
- **You use Music Assistant** and want additional audio endpoints
- **You need flexibility** - Different rooms, different requirements

## The Solution

Run a single Docker container on your NAS, Raspberry Pi, or Home Assistant server. Connect USB DACs or use built-in audio outputs. Each becomes an independent audio zone controllable from Music Assistant.

```
Your Server (NAS, Pi, HA, etc.)
         |
    [Container]
    /    |    \
 DAC1  DAC2  DAC3
   |     |     |
Kitchen Bedroom Patio
```

Players appear automatically in Music Assistant via the Sendspin protocol.

---

## Key Features

### For Everyone
- **Guided Setup Wizard** - First-run experience walks you through configuration
- **Web-Based Management** - Create, control, and monitor players from any browser
- **Auto-Discovery** - Players automatically appear in Music Assistant
- **Persistent Configuration** - Settings survive restarts and updates

### For Power Users
- **Custom Sinks** - Split multi-channel DACs or combine outputs
- **Sound Card Profiles** - Switch between stereo, multi-channel, and other modes
- **Device Volume Limits** - Set maximum volume limits per sound card for safety
- **Test Tones** - Verify audio routing during setup

### For Home Assistant Users
- **Native Add-on** - Installs from the add-on store
- **Sidebar Integration** - Access from your HA dashboard
- **PulseAudio Support** - Works with HA's audio system

---

## Quick Links

| I want to... | Go here |
|--------------|---------|
| See what's new in 5.0 | [What's New](WHATS_NEW_5.0) |
| Control amplifier power | [12V Triggers](12V-TRIGGERS) |
| Get running in 5 minutes | [Getting Started](GETTING_STARTED) |
| Use with Home Assistant | [HAOS Add-on Guide](HAOS_ADDON_GUIDE) |
| Split a multi-channel DAC | [Custom Sinks Guide](CUSTOM_SINKS) |
| Configure sound card modes | [Sound Card Setup](SOUND_CARD_SETUP) |
| Understand the code | [Code Structure](CODE_STRUCTURE) |
| Fix something broken | [Troubleshooting](#troubleshooting) |

---

## Quick Start

### Docker

```bash
docker run -d \
  --name multiroom-audio \
  -p 8096:8096 \
  --device /dev/snd:/dev/snd \
  ghcr.io/chrisuthe/multiroom-audio:latest
```

Then open `http://YOUR-SERVER-IP:8096` - the setup wizard will guide you through configuration.

### Home Assistant OS

1. Add repository: `https://github.com/chrisuthe/squeezelite-docker`
2. Install "Multi-Room Audio Controller" add-on
3. Start and open from sidebar
4. Follow the setup wizard

---

## Use Cases

### Basic: One DAC Per Room
Connect USB DACs to your server. Create one player per DAC. Each room is independently controllable.

### Intermediate: Multi-Channel DAC
Have a 4-channel or 8-channel DAC? Use **Custom Sinks** to split it into separate stereo zones. One DAC powers multiple rooms.

### Advanced: Grouped Zones
Create **Combine Sinks** to merge multiple outputs. Play the same audio on kitchen and dining room for open floor plans or party mode.

---

## Troubleshooting

### No audio devices found
- **Docker**: Add `--device /dev/snd:/dev/snd`
- **HAOS**: Restart add-on after connecting USB devices

### Custom ALSA devices not showing
If you have custom devices in `/etc/asound.conf` (dmix, virtual devices, etc.), mount the config file:
```yaml
volumes:
  - /etc/asound.conf:/etc/asound.conf:ro
```

### Player won't start
1. Try `null` device first (tests without audio hardware)
2. Check player logs in web interface
3. Verify Music Assistant is running

### Player not appearing in Music Assistant
1. Wait 30-60 seconds for discovery
2. Restart Music Assistant
3. Check both containers/add-ons are on the same network

---

## Links

- [GitHub Repository](https://github.com/chrisuthe/squeezelite-docker)
- [Report an Issue](https://github.com/chrisuthe/squeezelite-docker/issues)
