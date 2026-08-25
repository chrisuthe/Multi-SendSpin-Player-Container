# Docker Development Builds

Run development builds to test new features before they're released.

> **Warning:** Development builds may contain bugs, incomplete features, or breaking changes. Use stable builds for production.

---

## Quick Start

Pull and run the dev image:

```bash
docker run -d \
  --name multiroom-audio-dev \
  -p 8096:8096 \
  --device /dev/snd:/dev/snd \
  -v /proc/asound:/host/asound:ro \
  -v ./config:/app/config \
  ghcr.io/chrisuthe/multiroom-audio:dev
```

Or use Docker Compose:

```yaml
version: '3.8'

services:
  multiroom-audio:
    image: ghcr.io/chrisuthe/multiroom-audio:dev
    container_name: multiroom-audio-dev
    restart: unless-stopped
    ports:
      - "8096:8096"
    devices:
      - /dev/snd:/dev/snd
    volumes:
      - ./config:/app/config
      - ./logs:/app/logs
      - /proc/asound:/host/asound:ro
    network_mode: host
    cap_add:
      - SYS_NICE
```

---

## Development Environment Variables

These options are available in dev builds for testing and debugging:

| Variable | Default | Description |
|----------|---------|-------------|
| `MOCK_HARDWARE` | `false` | Simulate audio devices and relay boards without hardware |
| `LOG_LEVEL` | `info` | Logging verbosity: `debug`, `info`, `warning`, `error` |
| `PA_SAMPLE_RATE` | `48000` | PulseAudio sample rate (applied at container startup) |
| `PA_SAMPLE_FORMAT` | `float32le` | PulseAudio format: `s16le`, `s24le`, `s32le`, `float32le` |

### Example with Dev Options

```yaml
services:
  multiroom-audio:
    image: ghcr.io/chrisuthe/multiroom-audio:dev
    environment:
      - MOCK_HARDWARE=true
      - LOG_LEVEL=debug
    # ... rest of config
```

---

## Device Passthrough

### Audio Devices

```yaml
devices:
  - /dev/snd:/dev/snd
volumes:
  - /proc/asound:/host/asound:ro  # Enables device capability detection
cap_add:
  - SYS_NICE  # Required for audio thread priority
```

### 12V Trigger Relay Boards

Different relay board types require different device mappings:

**USB HID Relay Boards:**
```yaml
devices:
  - /dev/bus/usb:/dev/bus/usb    # Required: device discovery
  - /dev/hidraw0:/dev/hidraw0    # Required: relay control
  - /dev/hidraw1:/dev/hidraw1    # Add one per board
```

**FTDI Relay Boards:**
```yaml
devices:
  - /dev/bus/usb:/dev/bus/usb
cap_add:
  - SYS_RAWIO  # Only if kernel driver claims device
```

**Modbus/CH340 Relay Boards:**
```yaml
devices:
  - /dev/ttyUSB0:/dev/ttyUSB0
```

See [12V Triggers](12V-TRIGGERS) for detailed relay board setup.

---

## Mock Hardware Testing

Test without physical hardware by setting `MOCK_HARDWARE=true`. This simulates:

- 7 audio devices (USB DACs, Bluetooth, HDMI)
- 7 audio cards with profiles
- 5 relay boards (FTDI, HID, Modbus)

Customize mock devices by creating `config/mock_hardware.yaml`. See [Mock Hardware Configuration](MOCK_HARDWARE) for the full schema.

---

## Reporting Issues

When reporting issues with dev builds, include:

1. **Build version** — The commit SHA visible in the web UI footer or container logs
2. **Steps to reproduce** — What you did before the issue occurred
3. **Expected vs actual behavior** — What should happen vs what did happen
4. **Logs** — Set `LOG_LEVEL=debug` and include relevant log output
5. **Configuration** — Your Docker Compose or run command (redact sensitive info)

**Report issues:** [GitHub Issues](https://github.com/chrisuthe/Multi-SendSpin-Player-Container/issues)

---

## Updating Dev Builds

Dev builds are published on every push to the `dev` branch. To get the latest:

```bash
docker pull ghcr.io/chrisuthe/multiroom-audio:dev
docker compose down && docker compose up -d
```

---

## Building Locally

To build from source instead of pulling the dev image:

```bash
git clone https://github.com/chrisuthe/Multi-SendSpin-Player-Container.git
cd Multi-SendSpin-Player-Container
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build
```

See [Code Structure](CODE_STRUCTURE) for development documentation.

---

## See Also

- [Getting Started](GETTING_STARTED) — Stable build setup guide
- [12V Triggers](12V-TRIGGERS) — Relay board configuration
- [Mock Hardware](MOCK_HARDWARE) — Custom mock device configuration
- [What's New in 5.0](WHATS_NEW_5.0) — Features in development
