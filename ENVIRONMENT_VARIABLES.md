# Environment Variables

This document describes all environment variables supported by the Multi-Room Audio Controller application.

## Overview

The application validates environment variables on startup. Invalid values trigger warnings but do not crash the application - sensible defaults are used instead.

## Application Configuration

### WEB_PORT

Web server port number.

- **Type:** Integer
- **Default:** `8096`
- **Valid Range:** 1-65535

**Behavior differs between Docker and HAOS:**

| Environment | Notes |
|-------------|-------|
| **Standalone Docker** | Can be changed; update port mapping to match |
| **Home Assistant OS** | Must use port 8096 - ingress requires fixed port |

**Examples:**
```bash
# Default port
WEB_PORT=8096

# Custom port (Docker only - update port mapping)
WEB_PORT=8080
```

### LOG_LEVEL

Application logging verbosity.

- **Type:** String (enum)
- **Default:** `info`
- **Valid Values:** `debug`, `info`, `warning`, `error`
- **Description:** Controls how much detail is written to logs.

| Level | Use Case |
|-------|----------|
| `debug` | Troubleshooting issues, development |
| `info` | Normal operation |
| `warning` | Only show potential problems |
| `error` | Only show failures |

**Examples:**
```bash
# Verbose logging for troubleshooting
LOG_LEVEL=debug

# Normal operation (default)
LOG_LEVEL=info

# Quiet mode
LOG_LEVEL=error
```

### AUDIO_BACKEND

Audio backend selection.

- **Type:** String (enum)
- **Default:** Auto-detected
- **Valid Values:** `alsa`, `pulse`
- **Description:** Override the auto-detected audio backend. Normally detected based on environment (ALSA for standalone Docker, PulseAudio for Home Assistant OS).

**Examples:**
```bash
# ALSA (default for standalone Docker)
AUDIO_BACKEND=alsa

# PulseAudio (default for Home Assistant OS)
AUDIO_BACKEND=pulse
```

### ENABLE_ADVANCED_FORMATS

Enable per-player audio format selection UI (dev-only).

- **Type:** Boolean
- **Default:** `false`
- **Valid Values:** `true`, `false`, `1`, `0`, `yes`, `no`
- **Description:** Controls whether the advanced format selection UI is displayed. **Important:** All players default to advertising "flac-48000" for maximum Music Assistant compatibility regardless of this setting.

**Behavior:**

| Setting           | Default Format | UI Behavior                 | Use Case                                                                      |
|-------------------|----------------|-----------------------------|-------------------------------------------------------------------------------|
| `false` (default) | flac-48000     | No format dropdown shown    | Production - maximum MA compatibility                                         |
| `true`            | flac-48000     | Format dropdown shown in UI | Development/testing - allows selecting specific formats or "All Formats"      |

When enabled, the UI provides options to:

- Keep the default "flac-48000" (recommended)
- Select specific formats (e.g., "PCM 192kHz 32-bit", "FLAC 96kHz")
- Choose "All Formats" to advertise all supported formats

**Examples:**
```bash
# Production (default) - flac-48000, no UI dropdown
ENABLE_ADVANCED_FORMATS=false

# Development - flac-48000 default, but UI allows format selection
ENABLE_ADVANCED_FORMATS=true
```

### CONFIG_PATH

Configuration directory path.

- **Type:** String (directory path)
- **Default:** `/app/config` (Docker) or `/data` (HAOS)
- **Description:** Directory where player configurations are stored.

**Examples:**
```bash
# Custom config directory
CONFIG_PATH=/custom/config
```

### LOG_PATH

Log directory path.

- **Type:** String (directory path)
- **Default:** `/app/logs` (Docker) or `/share/multiroom-audio/logs` (HAOS)
- **Description:** Directory where application logs are written.

**Examples:**
```bash
# Custom log directory
LOG_PATH=/custom/logs
```

## Audio Configuration

### PA_SAMPLE_RATE

PulseAudio default sample rate (Docker mode only).

- **Type:** Integer
- **Default:** `48000`
- **Valid Values:** Common rates include 44100, 48000, 96000, 192000
- **Description:** Sets the default sample rate for PulseAudio in standalone Docker mode. This is applied at container startup before PulseAudio starts. Has no effect in HAOS mode (uses system PulseAudio).

**Examples:**
```bash
# Standard 48kHz (default)
PA_SAMPLE_RATE=48000

# Hi-res 192kHz
PA_SAMPLE_RATE=192000

# CD quality 44.1kHz
PA_SAMPLE_RATE=44100
```

### PA_SAMPLE_FORMAT

PulseAudio default sample format (Docker mode only).

- **Type:** String
- **Default:** `float32le`
- **Valid Values:** `s16le`, `s24le`, `s32le`, `float32le` (and big-endian variants)
- **Description:** Sets the default sample format for PulseAudio in standalone Docker mode. This is applied at container startup before PulseAudio starts. Has no effect in HAOS mode (uses system PulseAudio).

| Format | Bit Depth | Type | Use Case |
|--------|-----------|------|----------|
| `s16le` | 16-bit | Signed integer | Low CPU, basic quality |
| `s24le` | 24-bit | Signed integer | Good quality, moderate CPU |
| `s32le` | 32-bit | Signed integer | Hi-res, more CPU |
| `float32le` | 32-bit | Floating point | Default, best compatibility |

**Examples:**
```bash
# Default - float32 (best compatibility)
PA_SAMPLE_FORMAT=float32le

# Hi-res 32-bit signed integer
PA_SAMPLE_FORMAT=s32le

# Conservative 16-bit
PA_SAMPLE_FORMAT=s16le
```

**Note:** PulseAudio will automatically negotiate down to hardware capabilities if the requested format/rate isn't supported. These settings only affect the default target - actual output depends on DAC capabilities.

## HAOS-Specific Variables

### SUPERVISOR_TOKEN

Home Assistant supervisor authentication token.

- **Type:** String
- **Default:** Not set
- **Description:** Automatically set by Home Assistant OS when running as an add-on. Presence of this variable indicates HAOS mode.

**Note:** Do not set this manually. It is injected by the Home Assistant supervisor.

## Docker Compose Example

Here is a complete example showing all environment variables:

```yaml
version: '3.8'

services:
  multiroom-audio:
    image: ghcr.io/chrisuthe/multiroom-audio:latest
    environment:
      # Application Configuration
      WEB_PORT: "8096"
      LOG_LEVEL: "info"
      AUDIO_BACKEND: "alsa"
      CONFIG_PATH: "/app/config"
      LOG_PATH: "/app/logs"

      # Audio Configuration (Docker mode only)
      PA_SAMPLE_RATE: "48000"
      PA_SAMPLE_FORMAT: "float32le"
    volumes:
      - ./config:/app/config
      - ./logs:/app/logs
    devices:
      - /dev/snd:/dev/snd
    ports:
      - "8096:8096"
```

### Hi-Res Audio Example

For hi-res audio with capable DACs:

```yaml
version: '3.8'

services:
  multiroom-audio:
    image: ghcr.io/chrisuthe/multiroom-audio:latest
    environment:
      WEB_PORT: "8096"
      LOG_LEVEL: "info"

      # Hi-res audio: 192kHz, 32-bit
      PA_SAMPLE_RATE: "192000"
      PA_SAMPLE_FORMAT: "s32le"
    volumes:
      - ./config:/app/config
      - ./logs:/app/logs
    devices:
      - /dev/snd:/dev/snd
    ports:
      - "8096:8096"
```

## Environment Detection

The application automatically detects its runtime environment:

| Check | Result |
|-------|--------|
| `/data/options.json` exists | HAOS mode |
| `SUPERVISOR_TOKEN` set | HAOS mode |
| Neither | Docker mode |

### HAOS Mode Defaults

When running as a Home Assistant add-on:

| Variable | HAOS Default |
|----------|--------------|
| `CONFIG_PATH` | `/data` |
| `LOG_PATH` | `/share/multiroom-audio/logs` |
| `AUDIO_BACKEND` | `pulse` |

### Docker Mode Defaults

When running as standalone Docker:

| Variable | Docker Default |
|----------|----------------|
| `CONFIG_PATH` | `/app/config` |
| `LOG_PATH` | `/app/logs` |
| `AUDIO_BACKEND` | `alsa` |

## Common Issues

### Issue: "No audio devices found"

**Possible causes:**
- AUDIO_BACKEND set incorrectly
- Audio devices not passed to container

**Solution:**
- Let the application auto-detect the audio backend
- Ensure `--device /dev/snd:/dev/snd` is passed to Docker

### Issue: "Port already in use"

**Possible causes:**
- Another service using port 8096
- Previous container not stopped

**Solution:**
- Change WEB_PORT (Docker only)
- Stop conflicting service
- For HAOS: Port 8096 is required and cannot be changed
