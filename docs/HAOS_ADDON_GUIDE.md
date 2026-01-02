# Home Assistant OS Add-on Guide

Complete guide for running Multi-Room Audio Controller on Home Assistant OS.

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
- Uses Home Assistant's audio system (PulseAudio)
- Provides a web interface via HA's ingress system
- Automatically detects audio devices configured in HA

---

## Important: HAOS vs Docker

This add-on works differently than the standalone Docker container. Understand the differences before proceeding:

| Aspect | HAOS Add-on | Docker Container |
|--------|-------------|------------------|
| **Audio system** | PulseAudio (via hassio_audio) | ALSA (direct) |
| **Device names** | PA sink names | ALSA hw:X,Y format |
| **Config location** | `/data/` | `/app/config/` |
| **Web access** | HA Ingress (sidebar) | Direct port 8080 |
| **Network** | Host network (built-in) | Bridge or host mode |
| **Permissions** | Managed by HA | Manual --device flag |

**Key implication**: Audio device names and configuration differ between environments. Documentation written for Docker may not apply directly to HAOS.

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

---

## Audio Device Setup

### How HAOS Audio Works

Home Assistant OS uses PulseAudio through the `hassio_audio` service. This means:

1. Audio devices must be recognized by Home Assistant first
2. Devices appear as PulseAudio "sinks" (outputs)
3. Device names are longer than ALSA names (e.g., `alsa_output.usb-Generic_USB_Audio-00.analog-stereo`)

### Connecting a USB DAC

1. **Physically connect** the USB DAC to your HA server
2. **Wait 10 seconds** for the device to initialize
3. **Verify in HA**: Go to **Settings** > **System** > **Hardware**
4. Look for your device under **Audio**
5. **Restart the add-on** to detect the new device

### Viewing Available Devices

In the add-on web interface:
1. Click **Add Player**
2. The **Audio Device** dropdown shows all available PulseAudio sinks
3. Device names indicate the type:
   - `alsa_output.usb-...` = USB audio device
   - `alsa_output.platform-...` = Built-in audio (Pi, etc.)

### Device Name Examples

| Physical Device | PulseAudio Sink Name |
|-----------------|---------------------|
| Generic USB DAC | `alsa_output.usb-Generic_USB_Audio-00.analog-stereo` |
| Raspberry Pi headphone | `alsa_output.platform-bcm2835_audio.analog-stereo` |
| HDMI audio | `alsa_output.platform-fef00700.hdmi.hdmi-stereo` |

---

## Creating Players

### Step-by-Step

1. **Open the web interface** (sidebar or Open Web UI)
2. **Click "Add Player"**
3. **Configure the player:**

| Field | Description | Example |
|-------|-------------|---------|
| **Name** | Zone/room name | `Kitchen` |
| **Player Type** | Backend to use | `sendspin` |
| **Audio Device** | PulseAudio sink | (select from dropdown) |
| **Server IP** | For Squeezelite/Snapcast | Leave empty for auto-discovery |

4. **Click "Create Player"**
5. **Click "Start"** to begin playback

### Recommended Player Types for HAOS

| Your Setup | Recommended Type |
|------------|------------------|
| Music Assistant add-on | **Sendspin** |
| External LMS server | **Squeezelite** |
| Snapcast server | **Snapcast** |

---

## Integration with Music Assistant

If you run Music Assistant as an add-on (recommended setup):

### Automatic Discovery

1. Create a player with type **Sendspin**
2. Leave Server IP empty
3. Start the player
4. Within 30-60 seconds, the player appears in MA

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

### Edit Player

1. Click the **Edit** (pencil) icon
2. Modify settings
3. Click **Save**
4. Restart the player for changes to take effect

### Delete Player

1. Stop the player first
2. Click the **Delete** (trash) icon
3. Confirm deletion

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
4. Check volume isn't at 0

### Player Not Appearing in Music Assistant

**Cause**: Discovery blocked or timing issue

**Solution**:
1. Wait 60 seconds after starting the player
2. Restart Music Assistant add-on
3. Check both add-ons are using host network
4. For Squeezelite: try setting server IP to MA's IP

### Ingress Page Won't Load

**Cause**: Browser cache or port conflict

**Solution**:
1. Clear browser cache and cookies
2. Try a different browser
3. Try direct access: `http://homeassistant.local:8080`
4. Check if another add-on uses port 8080

---

## Log Locations

### Add-on Logs

1. Go to the add-on page
2. Click the **Log** tab
3. Scroll to see recent entries

### Player-Specific Logs

In the web interface, player logs can be viewed by clicking on the player name.

### SSH Access (Advanced)

```bash
# Player configs
/addon_configs/local_multiroom-audio/players.yaml

# Logs
/addon_configs/local_multiroom-audio/logs/
```

---

## Configuration Reference

### Add-on Options (config.yaml)

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `log_level` | string | `info` | Verbosity: debug, info, warning, error |

### Player Configuration

See Configuration Reference for full details on player settings.

---

## Network Ports

| Port | Protocol | Direction | Purpose |
|------|----------|-----------|---------|
| 8080 | TCP | Internal | Web interface (via ingress) |
| 3483 | TCP/UDP | Outbound | Squeezelite to LMS |
| 1704 | TCP | Outbound | Snapcast streaming |
| 1705 | TCP | Outbound | Snapcast control |

All outbound ports are used by players to connect to external servers. The add-on uses host networking, so no port mapping is needed.

---

## Known Limitations

1. **PulseAudio only**: Direct ALSA access is not available in HAOS
2. **Device names differ**: Documentation for Docker may show ALSA names (hw:0,0) that don't apply here
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

---

## Getting Help

If you're stuck:

1. **Check the logs** - Most issues are explained in the add-on logs
2. **Search existing issues** - Your problem may already be solved
3. **Open a new issue** - Include:
   - HA version
   - Add-on version
   - Relevant logs
   - Steps to reproduce

**GitHub Issues**: https://github.com/chrisuthe/squeezelite-docker/issues
