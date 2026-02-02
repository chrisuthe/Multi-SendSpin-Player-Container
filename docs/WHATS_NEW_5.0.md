# What's New in 5.0.0

**The power user release** - Multi-Room Audio Controller 5.0 brings 12V trigger support, improved reliability, and dozens of quality-of-life improvements.

---

## 12V Trigger Relay Control

The headline feature of 5.0: automatic amplifier power control.

### The Problem

You have external amplifiers powering your speakers. Every time you want to listen to music, you manually turn them on. Every time you forget to turn them off, they waste power.

### The Solution

Connect a USB relay board and let Multi-Room Audio handle it automatically:

1. **Playback starts** → Relay turns ON → Amplifier powers up
2. **Playback stops** → Configurable delay → Relay turns OFF → Amplifier powers down

### Supported Hardware

| Type | Models | Channels |
|------|--------|----------|
| **USB HID** | DCT Tech, ucreatefun | 1, 2, 4, or 8 |
| **FTDI** | Denkovi DAE-CB/Ro4-USB, DAE-CB/Ro8-USB | 4 or 8 |
| **Modbus/CH340** | Sainsmart 16-channel | 4, 8, or 16 |

### Multi-Board Support

Running multiple relay boards? No problem. Configure as many boards as you need - each maintains its own channel assignments and settings.

### Startup/Shutdown Behaviors

Control what happens when the service starts or stops:

- **All Off** (default) - Amplifiers start powered down (safest)
- **All On** - Amplifiers always powered
- **No Change** - Preserve current relay state

Configure in **Settings > 12V Triggers**. See the [12V Triggers Guide](12V-TRIGGERS) for complete setup instructions.

---

## Player Improvements

### Mute Button

New mute button on every player card. Click to mute, click again to unmute. Changes sync bidirectionally with Music Assistant - mute in either place and both update.

### Now Playing Info

Click a player name to see the Player Details modal, now showing:

- **Track Info** - Title, artist, album, and artwork
- **Device Capabilities** - Supported sample rates, bit depths, channel count
- **Connection Details** - Server address, discovery method, advertised format

### International Character Support

Player names now support Unicode characters:

- Emojis: "Kitchen 🎵"
- CJK characters: "客厅音响"
- Accented characters: "Habitación Principal"

Name your players however makes sense to you.

### Volume Persistence

Volume settings now survive container restarts. Set your preferred volume once, and it stays.

### Startup Volume

"Initial Volume" has been renamed to "Startup Volume" for clarity. This is the volume applied when a player connects to Music Assistant.

---

## Reconnection & Reliability

### Startup Progress Overlay

When the add-on starts, a progress overlay shows connection status. No more wondering if it's working.

### WaitingForServer State

If Music Assistant is unavailable, players now show a clear "Waiting for Server" state instead of appearing broken.

### mDNS Watch

Players automatically reconnect when Music Assistant comes back online. Server restart? Network hiccup? Players recover without intervention.

### Graceful Shutdown

Clean disconnection from Music Assistant when stopping, ensuring proper state cleanup.

---

## Sync Improvements

### Anti-Oscillation Debounce

Previous versions could oscillate when correcting sync drift. The new debounce algorithm prevents over-correction.

### Latency Lock-In

Once sync is achieved, small variations from PulseAudio timing noise are ignored. Only genuine drift triggers correction.

### 15ms Correction Threshold

Sync corrections only apply when drift exceeds 15ms. Smaller variations are normal and don't affect perceived synchronization.

---

## Custom Sinks

### Mono Output Mode

Remap sinks now support mono (single channel) output. Perfect for:

- Mono PA systems
- Single-speaker zones
- Summing stereo to mono for specific applications

---

## Audio Devices

### Editable Alias

Give your audio devices friendly names. Click the device in Settings, enter an alias, and that name appears throughout the UI instead of the hardware identifier.

---

## Under the Hood

For the technically curious:

- **SendSpin.SDK 6.1.1** - Major SDK upgrade from 5.2.0 with improved protocol handling
- **Improved error handling** - Typed exceptions and better error messages
- **Better PulseAudio integration** - Fixed card/sink matching issues
- **Performance optimizations** - Cached device info, incremental UI updates
- **Thread safety improvements** - ReaderWriterLockSlim patterns

---

## Getting Started with 5.0

| I want to... | Do this |
|--------------|---------|
| Control amplifier power | Settings > 12V Triggers > Add Relay Board |
| Mute a player | Click the speaker icon on the player card |
| See what's playing | Click the player name |
| Name my audio device | Settings > Audio Devices > Click device > Set alias |
| Create mono output | Settings > Custom Sinks > Add Remap Sink > Select mono channel |

---

## For the Upgraders

### Breaking Changes

None! 5.0 is backward-compatible with your existing configuration.

### What Happens on First Launch

1. Existing players continue working unchanged
2. 12V triggers are disabled by default (opt-in feature)
3. All new features available in Settings menu

---

## Thank You

This release includes significant contributions from the community. Special thanks to everyone who reported issues, tested dev builds, and provided feedback.

Found a bug? Have a suggestion? [Open an issue](https://github.com/chrisuthe/Multi-SendSpin-Player-Container/issues).

---

*Multi-Room Audio Controller 5.0 - Power management for the power user.*
