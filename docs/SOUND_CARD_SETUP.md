# Sound Card Setup Guide

Sound Card Setup allows you to configure audio card profiles - selecting between different operational modes supported by your hardware. This is essential for multi-channel DACs, USB audio interfaces, and devices with multiple configuration options.

> **Requires**: Version 4.0.0 or later

---

## Overview

### What Are Card Profiles?

Many audio devices support multiple operational modes called "profiles." A USB audio interface might offer:

- **Analog Stereo Output** - 2-channel output only
- **Analog Stereo Duplex** - 2-channel input and output
- **Analog Surround 4.0** - 4-channel output
- **Analog Surround 5.1** - 6-channel output
- **Pro Audio** - All channels available (pro interfaces)
- **Off** - Device disabled

The active profile determines:
- How many channels are available
- Which inputs/outputs are active
- Sample rate and format options

### Why Change Profiles?

| Scenario | Profile Change |
|----------|----------------|
| Multi-channel DAC showing only stereo | Switch to surround/multi-channel profile |
| Device not appearing as output | Switch from input-only to output profile |
| Need more channels for remap sinks | Switch to higher channel count profile |
| Device conflicts with other software | Switch to Off, then back |

### Accessing Sound Card Setup

1. Open the web interface
2. Click **Settings** (gear icon in header)
3. Select **Sound Card Setup**

---

## Understanding the Interface

### Card List

The Sound Card Setup page displays all detected audio cards:

```
[0] USB Audio Device
    HiFiBerry DAC+ Pro
    Active Profile: Analog Stereo Output
    Available Profiles: 3

[1] Built-in Audio
    Intel PCH
    Active Profile: Analog Stereo Duplex
    Available Profiles: 5
```

Each card shows:
- **Index** - PulseAudio card number
- **Name** - System identifier
- **Description** - Human-readable device name
- **Active Profile** - Currently selected mode
- **Available Profiles** - Number of supported modes

### Profile Details

Click on a card to expand its profile list:

| Profile | Channels | Available | Status |
|---------|----------|-----------|--------|
| Analog Stereo Output | 2 | Yes | Active |
| Analog Surround 4.0 | 4 | Yes | - |
| Analog Surround 5.1 | 6 | No* | - |
| Off | 0 | Yes | - |

*Profiles may be unavailable if hardware doesn't support them

---

## Changing Profiles

### Basic Steps

1. Go to **Settings > Sound Card Setup**
2. Find your audio card in the list
3. Click on the card to expand profile options
4. Click **Select** next to the desired profile
5. Wait for confirmation (1-2 seconds)

### What Happens When You Change

1. PulseAudio switches the card to the new profile
2. Old sinks (outputs) are removed
3. New sinks are created for the profile
4. Any sinks created by the new profile are automatically unmuted (unless a boot mute preference is set)
5. The selection is saved for persistence across restarts

### Profile Persistence

Changed profiles are:
- Saved to `card-profiles.yaml` in the config directory
- Automatically restored when the container/add-on starts
- Applied before custom sinks are loaded

### Boot Mute Preference

Each card can also be configured to start **Muted** or **Unmuted** on boot. The preference is:
- Saved alongside the profile in `card-profiles.yaml`
- Applied after profiles are restored (so it affects the new sinks)
- Independent from manual mute/unmute actions in the UI

---

## Common Scenarios

### Enabling Multi-Channel Output

**Problem**: Your 4-channel DAC only shows as stereo.

**Solution**:
1. Open Sound Card Setup
2. Find your DAC
3. Look for profiles like:
   - "Analog Surround 4.0"
   - "Analog Surround 5.1"
   - "Pro Audio"
4. Select the multi-channel profile
5. New outputs appear with additional channels

### USB Audio Interface Setup

**Problem**: Pro audio interface shows too many or wrong outputs.

**Solution**:
1. Open Sound Card Setup
2. Try different profiles:
   - "Analog Stereo Output" - Simple 2-channel
   - "Pro Audio" - All channels, maximum flexibility
3. Choose based on your needs

### Device Not Appearing

**Problem**: Audio device not listed.

**Checklist**:
1. Is the device physically connected?
2. Does it appear in `Settings > System > Hardware` (HAOS)?
3. Have you restarted the add-on/container since connecting?

**For USB devices**:
- Try a different USB port
- Use a powered USB hub for power-hungry devices
- Check USB cable quality

---

## Profile Reference

### Common Profile Types

| Profile Name | Description |
|--------------|-------------|
| Analog Stereo Output | 2-channel output only |
| Analog Stereo Duplex | 2-channel input + output |
| Analog Surround 4.0 | 4-channel output (quad) |
| Analog Surround 5.1 | 6-channel output |
| Analog Surround 7.1 | 8-channel output |
| Pro Audio | All available I/O (pro interfaces) |
| Digital Stereo (IEC958) | S/PDIF or optical output |
| HDMI | HDMI audio output |
| Off | Device disabled |

### Profile Availability

Not all profiles are available on all devices:

- **Hardware limitation**: Device doesn't support that mode
- **Connection type**: Some modes require specific cables
- **Driver support**: Linux driver may not expose all modes

Unavailable profiles are shown but cannot be selected.

---

## Troubleshooting

### Profile Change Has No Effect

1. Wait 2-3 seconds for PulseAudio to reconfigure
2. Refresh the Custom Sinks or Add Player dialog
3. Check logs for errors

### Players Stop Working After Profile Change

Changing a card's profile removes its old sinks. If a player was using one of those sinks:

1. The player will show an error state
2. Edit the player to select the new sink
3. Or delete and recreate the player

**Tip**: Note which sinks players use before changing profiles.

### Profile Not Persisting

Check if:
1. Config directory is writable
2. `card-profiles.yaml` exists after making a change
3. Container/add-on has proper permissions

### New Sinks Are Muted

After a profile change, new sinks may start muted. The app automatically unmutes them, but if you still have no audio:

1. Check volume isn't at 0
2. Check the specific sink in your system mixer
3. Try `pactl set-sink-mute <sink-name> 0` via SSH

---

## Integration with Custom Sinks

Sound Card profiles and Custom Sinks work together:

### Recommended Order

1. **First**: Configure card profiles to expose needed channels
2. **Then**: Create remap sinks to split multi-channel cards
3. **Finally**: Create combine sinks if needed
4. **Last**: Create players using your configured sinks

### Example Workflow

Goal: Split a 4-channel DAC into two stereo zones

1. **Check profile**: Ensure card is in "Analog Surround 4.0" profile
2. **Create remap sink 1**: Channels 1-2 → "Living Room"
3. **Create remap sink 2**: Channels 3-4 → "Bedroom"
4. **Create players**: One for each remap sink

---

## API Reference

### Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/cards` | List all sound cards |
| GET | `/api/cards/{id}` | Get card details |
| PUT | `/api/cards/{id}/profile` | Set card profile |
| PUT | `/api/cards/{id}/boot-mute` | Set boot mute preference |
| PUT | `/api/cards/{id}/mute` | Mute or unmute a card in real time |

### Example Request

```bash
# Set card profile
curl -X PUT http://localhost:8096/api/cards/0/profile \
  -H "Content-Type: application/json" \
  -d '{"profile": "analog-surround-40"}'
```

---

## Technical Details

### How It Works

1. The app uses `pactl list cards` to enumerate available cards
2. Profile changes use `pactl set-card-profile`
3. Configurations are persisted to `card-profiles.yaml`
4. On startup, saved profiles are restored before any custom sinks load

### Configuration File Format

```yaml
alsa_card.usb-MyDAC:
  card_name: alsa_card.usb-MyDAC
  profile_name: analog-surround-40
```

### Relationship to ALSA

On HAOS and systems using PulseAudio:
- PulseAudio manages cards and profiles
- ALSA is the underlying driver
- Profile names come from PulseAudio's abstraction

On standalone Docker with ALSA:
- Card profiles may not be available
- Direct ALSA configuration is used instead

---

## See Also

- [Custom Sinks Guide](CUSTOM_SINKS) - Create virtual outputs after configuring cards
- [Getting Started](GETTING_STARTED) - Basic setup guide
- [HAOS Add-on Guide](HAOS_ADDON_GUIDE) - Home Assistant specific setup
- [What's New in 4.0](WHATS_NEW_4.0) - Overview of new features
