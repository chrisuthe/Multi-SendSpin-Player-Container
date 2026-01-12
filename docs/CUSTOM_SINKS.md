# Custom Sinks Guide

Custom Sinks allow you to create virtual audio outputs that combine, split, or remap physical audio devices. This enables advanced multi-room configurations using multi-channel DACs or grouped speaker zones.

> **Requires**: Version 4.0.0 or later

---

## Overview

### What Are Custom Sinks?

PulseAudio (the audio system used by HAOS and many Linux configurations) supports "virtual sinks" - software-defined audio outputs that process audio before routing it to physical hardware. Multi-Room Audio Controller provides a web interface for creating and managing two types:

| Sink Type | Purpose | Use Case |
|-----------|---------|----------|
| **Combine Sink** | Routes audio to multiple outputs simultaneously | Party mode, open floor plans |
| **Remap Sink** | Extracts specific channels from a multi-channel device | Split 4-channel DAC into 2 stereo zones |

### Accessing Custom Sinks

1. Open the web interface
2. Click **Settings** (gear icon in header)
3. Select **Custom Sinks**

---

## Combine Sinks

A combine sink plays the same audio on multiple physical outputs at the same time.

### When to Use

- **Party mode**: All speakers play together
- **Open floor plans**: Kitchen and living room as one zone
- **Redundancy**: Critical announcements on multiple speakers

### Creating a Combine Sink

1. Go to **Settings > Custom Sinks**
2. Click **Add Combine Sink**
3. Fill in the form:

| Field | Required | Description |
|-------|----------|-------------|
| **Name** | Yes | Unique identifier (letters, numbers, underscores, hyphens, dots only) |
| **Description** | No | Human-readable label shown in device dropdown |
| **Slave Sinks** | Yes | Select 2 or more output devices to combine |

4. Click **Create**
5. Use the **Test** button to verify audio plays on all selected outputs

### Example: Kitchen + Dining Room

```
Name: kitchen_dining_combined
Description: Kitchen & Dining Room
Slave Sinks:
  - alsa_output.usb-Kitchen_DAC
  - alsa_output.usb-Dining_DAC
```

Result: A new sink called `kitchen_dining_combined` appears in the device dropdown. Players using this sink output to both DACs simultaneously.

### Technical Details

Under the hood, this creates a PulseAudio `module-combine-sink`:

```
pactl load-module module-combine-sink \
  sink_name=kitchen_dining_combined \
  sink_properties=device.description="Kitchen & Dining Room" \
  slaves=alsa_output.usb-Kitchen_DAC,alsa_output.usb-Dining_DAC
```

---

## Remap Sinks

A remap sink extracts specific channels from a multi-channel device and presents them as a new stereo (or mono) output.

### When to Use

- **Multi-channel DACs**: Split a 4-channel USB DAC into 2 stereo zones
- **Pro audio interfaces**: Use channels 3-4 of an 8-channel interface as a separate output
- **Surround receivers**: Repurpose rear channels as a second zone

### Creating a Remap Sink

1. Go to **Settings > Custom Sinks**
2. Click **Add Remap Sink**
3. Fill in the form:

| Field | Required | Description |
|-------|----------|-------------|
| **Name** | Yes | Unique identifier |
| **Description** | No | Human-readable label |
| **Master Sink** | Yes | The physical multi-channel device |
| **Channels** | Yes | Number of output channels (usually 2 for stereo) |
| **Channel Mappings** | Yes | Which master channels map to which output channels |
| **Remix** | No | Enable mixing (usually leave disabled) |

4. Click **Create**
5. Use the **Test** button to verify audio plays on the correct channels

### Example: 4-Channel DAC Split

A 4-channel USB DAC with channels:
- Channels 1-2 (front-left, front-right) → Living Room
- Channels 3-4 (rear-left, rear-right) → Bedroom

**Living Room Sink** (channels 1-2):
```
Name: living_room
Description: Living Room
Master Sink: alsa_output.usb-4ch_DAC
Channels: 2
Channel Mappings:
  - Output: front-left  → Master: front-left
  - Output: front-right → Master: front-right
```

**Bedroom Sink** (channels 3-4):
```
Name: bedroom
Description: Bedroom
Master Sink: alsa_output.usb-4ch_DAC
Channels: 2
Channel Mappings:
  - Output: front-left  → Master: rear-left
  - Output: front-right → Master: rear-right
```

### Channel Names Reference

Standard PulseAudio channel names:

| Channels | Names |
|----------|-------|
| Stereo | `front-left`, `front-right` |
| Quad | `front-left`, `front-right`, `rear-left`, `rear-right` |
| 5.1 | `front-left`, `front-right`, `front-center`, `lfe`, `rear-left`, `rear-right` |
| 7.1 | Above + `side-left`, `side-right` |

### Technical Details

Under the hood, this creates a PulseAudio `module-remap-sink`:

```
pactl load-module module-remap-sink \
  sink_name=bedroom \
  sink_properties=device.description="Bedroom" \
  master=alsa_output.usb-4ch_DAC \
  channels=2 \
  channel_map=front-left,front-right \
  master_channel_map=rear-left,rear-right \
  remix=no
```

---

## Managing Sinks

### Viewing Sinks

The Custom Sinks page shows all created sinks with their:
- Name and description
- Type (Combine or Remap)
- State (Loaded, Error, etc.)
- Configuration details

### Testing Sinks

Click the **Test** button on any sink to play a test tone. This helps verify:
- Audio is routed correctly
- The right speakers are playing
- Channel mapping is correct (for remap sinks)

### Deleting Sinks

1. Click the **Delete** (trash) icon on the sink
2. Confirm deletion

**Note**: Delete any players using the sink first, or they will fail to start.

### Error States

| State | Meaning | Action |
|-------|---------|--------|
| **Loaded** | Sink is active and working | None needed |
| **Error** | Failed to load | Check error message, verify master sink exists |
| **Loading** | Currently being created | Wait a moment |

Common errors:
- **Master sink not found**: The physical device isn't available. Check it's connected.
- **Slave sink not found**: One of the combined sinks isn't available.
- **Name already exists**: A PulseAudio sink with this name already exists.

---

## Importing Existing Sinks

If you have custom sinks defined in PulseAudio's `default.pa` configuration, you can import them into the app for management:

1. Go to **Settings > Custom Sinks**
2. Click **Import from default.pa**
3. Select the sinks you want to import
4. Click **Import**

This allows you to manage previously-created sinks through the web interface without recreating them.

---

## Best Practices

### Naming Conventions

- Use lowercase letters with underscores: `living_room`, `kitchen_dining`
- Avoid spaces and special characters
- Keep names short but descriptive

### Order of Creation

When creating remap sinks from a multi-channel device:
1. Create all remap sinks first
2. Then create players that use them

When creating combine sinks:
1. Ensure all slave sinks exist and are working
2. Then create the combine sink

### Persistence

Custom sinks are:
- Saved to `custom-sinks.yaml` in the config directory
- Automatically recreated on container/add-on restart
- Independent of PulseAudio's `default.pa`

---

## Troubleshooting

### Sink Not Appearing in Player Device List

1. Wait a few seconds after creation
2. Refresh the Add Player dialog
3. Check the sink state is "Loaded"

### No Audio on Combined Sink

1. Test each slave sink individually
2. Verify both physical devices are connected
3. Check none of the outputs are muted

### Wrong Channels Playing (Remap Sink)

1. Verify the master sink has the expected channel count
2. Double-check channel mapping configuration
3. Use Test button to identify which channels are actually playing

### Sink Fails to Load After Restart

The physical device might be unavailable at startup. Check:
1. USB devices are connected before container starts
2. Device names haven't changed (some USB devices enumerate differently)

---

## API Reference

For programmatic access, see the API documentation at `/docs` in your installation.

### Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sinks` | List all custom sinks |
| POST | `/api/sinks/combine` | Create combine sink |
| POST | `/api/sinks/remap` | Create remap sink |
| GET | `/api/sinks/{name}` | Get sink details |
| DELETE | `/api/sinks/{name}` | Delete sink |
| POST | `/api/sinks/{name}/test` | Play test tone |

---

## See Also

- [Sound Card Setup](SOUND_CARD_SETUP) - Configure card profiles before creating sinks
- [Getting Started](GETTING_STARTED) - Basic setup guide
- [What's New in 4.0](WHATS_NEW_4.0) - Overview of new features
