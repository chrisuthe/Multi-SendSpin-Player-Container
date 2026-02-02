# Home Assistant OS Add-on Guide

Complete guide for running Multi-Room Audio Controller on Home Assistant OS.

> **Version 4.0**: Now includes a guided setup wizard, custom sinks for multi-channel DACs, sound card profile management, and device-level volume limits. [See what's new](WHATS_NEW_4.0).

---

## The Problem

You run Home Assistant OS and want multi-room audio that:

- Integrates natively with your HA installation
- Appears in the HA sidebar for easy access
- Uses USB DACs connected to your HA server
- Works seamlessly with Music Assistant add-on

## The Solution

Install the Multi-Room Audio Controller as a native HAOS add-on. It:

- Runs alongside your other add-ons
- Uses Home Assistant's audio system
- Provides a web interface via HA's ingress system
- Automatically detects audio devices
- Creates Sendspin players that appear in Music Assistant

---

## Important: HAOS vs Docker

This add-on works differently than the standalone Docker container. Understand the differences before proceeding:

| Aspect | HAOS Add-on | Docker Container |
|--------|-------------|------------------|
| **Audio system** | PulseAudio (via hassio_audio) | ALSA (direct) |
| **Device names** | PA sink names | ALSA hw:X,Y format |
| **Config location** | `/data/` | `/app/config/` |
| **Web access** | HA Ingress (sidebar) | Direct port 8096 |
| **Network** | Host network (built-in) | Bridge or host mode |
| **Permissions** | Managed by HA | Manual --device flag |

**Key implication**: Audio device names and configuration differ between environments.

---

## Prerequisites

- Home Assistant OS or Home Assistant Supervised
- (Recommended) Music Assistant add-on installed
- (Optional) USB DAC connected to your HA server

### Not Compatible With

- Home Assistant Container (use Docker deployment instead)
- Home Assistant Core (use Docker deployment instead)

---

## Installation

### Step 1: Add the Repository

1. Navigate to **Settings** > **Add-ons** > **Add-on Store**
2. Click the **three-dot menu** (top right corner)
3. Select **Repositories**
4. Enter: `https://github.com/chrisuthe/squeezelite-docker`
5. Click **Add**
6. Click **Close**

### Step 2: Install the Add-on

1. The add-on store should refresh automatically
2. Scroll down or search for **"Multi-Room Audio Controller"**
3. Click on the add-on
4. Click **Install**
5. Wait for installation (may take 1-2 minutes)

### Step 3: Configure (Optional)

Default configuration works for most users. Available options:

```yaml
log_level: info  # Options: debug, info, warning, error
```

To change:
1. Go to the add-on's **Configuration** tab
2. Modify the YAML
3. Click **Save**

### Step 4: Start the Add-on

1. Go to the **Info** tab
2. Click **Start**
3. Wait for the log to show "Application startup complete"

### Step 5: Access the Interface

**Option A - Sidebar (Recommended)**
1. Enable **Show in sidebar** on the Info tab
2. Click **Multi-Room Audio** in your HA sidebar

**Option B - Ingress**
1. Click **Open Web UI** on the Info tab

### Step 6: Complete the Setup Wizard (First-Time Only)

On first launch, the Setup Wizard automatically guides you through configuration:

1. **Welcome** - Overview of multi-room audio setup
2. **Hardware Detection** - Review detected audio cards and devices
3. **Sound Card Configuration** - Set profiles for multi-channel devices
4. **Player Creation** - Create players for each zone/room
5. **Audio Testing** - Verify audio plays on correct outputs
6. **Complete** - Start using your multi-room system

**Tips:**
- Skip any step you want to configure later
- Re-run the wizard anytime from **Settings > Run Setup Wizard**
- The wizard only appears for new installations (no existing players)

---

## Audio Device Setup

### How HAOS Audio Works

Home Assistant OS uses PulseAudio through the `hassio_audio` service. The v2.0 C# rewrite uses PortAudio for audio output, which automatically bridges to PulseAudio via ALSA configuration. This means:

1. Audio devices must be recognized by Home Assistant first
2. Devices appear as PortAudio outputs (which route through PulseAudio on HAOS)
3. Device names reflect PortAudio's enumeration format

### Connecting a USB DAC

1. **Physically connect** the USB DAC to your HA server
2. **Wait 10 seconds** for the device to initialize
3. **Verify in HA**: Go to **Settings** > **System** > **Hardware**
4. Look for your device under **Audio**
5. **Restart the add-on** to detect the new device

### Viewing Available Devices

In the add-on web interface:
1. Click **Add Player**
2. The **Audio Device** dropdown shows all available devices
3. Device names are provided by PortAudio and typically include the device description

---

## Creating Players

### Step-by-Step

1. **Open the web interface** (sidebar or Open Web UI)
2. **Click "Add Player"**
3. **Configure the player:**

| Field | Description | Example |
|-------|-------------|---------|
| **Name** | Zone/room name | `Kitchen` |
| **Audio Device** | Select from dropdown | (select your device) |
| **Server IP** | Optional: Music Assistant IP | Leave empty for auto-discovery |

4. **Click "Create Player"**
5. **Click "Start"** to begin

Players automatically appear in Music Assistant within 30-60 seconds.

---

## Integration with Music Assistant

If you run Music Assistant as an add-on (recommended setup):

### Automatic Discovery

1. Create a player in this add-on
2. Start the player
3. Within 30-60 seconds, the player appears in Music Assistant

### Verification

1. Open Music Assistant
2. Go to **Settings** > **Players**
3. Your player should be listed and available

### If Discovery Fails

1. **Restart Music Assistant** after creating the player
2. **Check network**: Both add-ons should be on host network (default)
3. **Check logs**: Look for mDNS errors in the add-on logs

---

## Managing Players

### Start/Stop

- Click **Start** or **Stop** button on each player card
- Status indicator shows running (green) or stopped (gray)

### Volume Control

- Use the slider on each player card
- Volume changes are immediate
- Volume is saved and persists across restarts

### Delay Offset

- Adjust timing for multi-room synchronization
- Positive values delay audio (if this room is ahead)
- Measured in milliseconds

### Delete Player

1. Stop the player first
2. Click the **Delete** (trash) icon
3. Confirm deletion

---

## Advanced Features (4.0+)

### Sound Card Profiles

Many USB DACs and audio interfaces support multiple operational modes. Access **Settings > Sound Card Setup** to:

- View all detected sound cards
- See available profiles (stereo, surround, pro audio, etc.)
- Switch profiles to enable multi-channel output
- Profiles persist across reboots

**Common use case**: Your multi-channel DAC shows only stereo. Switch to "Analog Surround 4.0" or "Pro Audio" profile to enable all channels.

See [Sound Card Setup Guide](SOUND_CARD_SETUP) for detailed instructions.

### Custom Sinks

Create virtual audio outputs for advanced configurations. Access **Settings > Custom Sinks** to create:

**Combine Sinks** - Play audio on multiple outputs simultaneously
- Party mode (all rooms together)
- Open floor plans
- Redundant announcements

**Remap Sinks** - Extract channels from multi-channel devices
- Split 4-channel DAC into 2 stereo zones
- Use surround receiver as multi-zone amp
- Route specific channels to specific outputs

See [Custom Sinks Guide](CUSTOM_SINKS) for step-by-step instructions.

### Device Volume Limits

Set maximum volume limits per sound card for safety:

- Navigate to Settings > Sound Cards
- Use the **Limit Max. Vol.** slider for each card
- Volume limit is applied at the device level
- Prevents accidental over-driving of speakers

Settings persist across restarts and are applied automatically at startup.

---

## Troubleshooting

### Device Not Appearing in Dropdown

**Cause**: USB device not recognized or add-on needs restart

**Solution**:
1. Check **Settings** > **System** > **Hardware** for the device
2. If not there, try a different USB port
3. Restart the add-on
4. Check add-on logs for audio errors

### Player Won't Start

**Cause**: Device busy, missing, or incompatible

**Solution**:
1. Check add-on logs (**Log** tab on add-on page)
2. Try creating a test player with `null` device
3. Ensure no other add-on is using the audio device
4. Try restarting the add-on

### Player Starts But No Sound

**Cause**: Wrong device selected or device muted

**Solution**:
1. Verify you selected an output device (not input/monitor)
2. Check physical connections (DAC -> amp -> speakers)
3. Test the device using HA's audio test feature
4. Check volume is not at 0

### Player Not Appearing in Music Assistant

**Cause**: Discovery blocked or timing issue

**Solution**:
1. Wait 60 seconds after starting the player
2. Restart Music Assistant add-on
3. Check both add-ons are using host network

### Ingress Page Won't Load

**Cause**: Browser cache or port conflict

**Solution**:
1. Clear browser cache and cookies
2. Try a different browser
3. Try direct access: `http://homeassistant.local:8096`
4. Check if another add-on uses port 8096

---

## Log Locations

### Add-on Logs

1. Go to the add-on page
2. Click the **Log** tab
3. Scroll to see recent entries

### SSH Access (Advanced)

```bash
# Player configs (HAOS add-on data directory)
/data/players.yaml

# Logs (shared directory)
/share/multiroom-audio/logs/
```

---

## Configuration Reference

### Add-on Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `log_level` | string | `info` | Verbosity: debug, info, warning, error |
| `relay_serial_port` | device | null | Serial port for Modbus/CH340 relay board |
| `relay_devices` | list | `[]` | Device paths for HID/FTDI relay boards |

---

## Network Ports

| Port | Protocol | Direction | Purpose |
|------|----------|-----------|---------|
| 8096 | TCP | Internal | Web interface (via ingress) |

All player communication uses mDNS for discovery and the Sendspin protocol for streaming. The add-on uses host networking, so no port mapping is needed.

### Important: Port 8096 is Fixed

Unlike standalone Docker deployments, **HAOS add-ons cannot use dynamic port switching**. The ingress system requires a fixed port configured in `config.yaml`.

---

## Known Limitations

1. **Sendspin only**: Only Music Assistant via Sendspin protocol is supported
2. **PortAudio via PulseAudio**: Audio routes through PulseAudio on HAOS (handled automatically)
3. **Full access required**: The add-on needs elevated permissions for audio device access
4. **USB hot-plug**: Adding USB devices requires add-on restart to detect

---

## FAQ

### Can I use this with Home Assistant Container?

No. Use the Docker deployment instead, which works with HA Container.

### Why are device names so long?

HAOS uses PulseAudio, which has descriptive sink names. This is normal and helps identify devices.

### Can I run multiple instances?

No. One add-on instance manages all players. Create multiple players within the single instance.

### Does this work with Bluetooth speakers?

Bluetooth audio in HAOS is limited. Check HA's Bluetooth integration first. If your speaker appears as a PulseAudio sink, it may work.

### How do I split a multi-channel DAC?

1. Go to **Settings > Sound Card Setup** and enable a multi-channel profile
2. Go to **Settings > Custom Sinks** and create Remap Sinks for each stereo pair
3. Create players using the new remap sinks

See [Custom Sinks Guide](CUSTOM_SINKS) for detailed walkthrough.

### Can I play the same audio on multiple outputs?

Yes! Create a Combine Sink in **Settings > Custom Sinks**. Select multiple outputs to combine, then create a player using the combined sink.

### The setup wizard didn't appear

The wizard only shows for new installations with no existing players. To run it manually:
1. Go to **Settings > Run Setup Wizard**, or
2. Go to **Settings > Reset First-Run State** then refresh the page

---

## Getting Help

If you are stuck:

1. **Check the logs** - Most issues are explained in the add-on logs
2. **Search existing issues** - Your problem may already be solved
3. **Open a new issue** - Include:
   - HA version
   - Add-on version
   - Relevant logs
   - Steps to reproduce

**GitHub Issues**: https://github.com/chrisuthe/squeezelite-docker/issues
