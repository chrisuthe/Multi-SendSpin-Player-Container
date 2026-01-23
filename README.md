# Multi-Room Audio Controller

<!-- markdownlint-disable MD033 -->
<p align="center">
  <img src="multiroom.jpg" alt="Multi-Room Audio Controller" width="400">
</p>
<!-- markdownlint-enable MD033 -->

## The Core Concept

**One server. Multiple audio outputs. Whole-home audio with Music Assistant.**

This project enables you to run a single centralized server (like a NAS, Raspberry Pi, or any Docker host) with multiple USB DACs or audio devices connected, creating independent audio zones throughout your home. Instead of buying expensive multi-room audio hardware, connect affordable USB DACs to a central machine and stream synchronized audio to every room.

```
┌─────────────────────────────────────────────────────────────────┐
│                     CENTRAL SERVER (Docker Host)                │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │              Multi-Room Audio Container                 │    │
│  │                                                         │    │
│  │   ┌──────────┐  ┌──────────┐  ┌──────────┐              │    │
│  │   │ Player 1 │  │ Player 2 │  │ Player 3 │  ...         │    │
│  │   │(Kitchen) │  │(Bedroom) │  │ (Patio)  │              │    │
│  │   └────┬─────┘  └────┬─────┘  └────┬─────┘              │    │
│  └────────┼─────────────┼─────────────┼────────────────────┘    │
│           │             │             │                         │
│      ┌────▼────┐   ┌────▼────┐   ┌────▼────┐                    │
│      │USB DAC 1│   │USB DAC 2│   │SoundCard│                    │
│      └────┬────┘   └────┬────┘   └────┬────┘                    │
└───────────┼─────────────┼─────────────┼─────────────────────────┘
            │             │             │
       ┌────▼────┐   ┌────▼────┐   ┌────▼────┐
       │ Kitchen │   │ Bedroom │   │  Patio  │
       │Speakers │   │Speakers │   │Speakers │
       └─────────┘   └─────────┘   └─────────┘
```

</div>

![Multi-Room Audio Controller](https://img.shields.io/badge/Multi--Room-Audio%20Controller-blue?style=for-the-badge&logo=music)
![Docker](https://img.shields.io/badge/Docker-Containerized-2496ED?style=for-the-badge&logo=docker)
![Music Assistant](https://img.shields.io/badge/Music%20Assistant-Compatible-green?style=for-the-badge)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet)
![Sendspin](https://img.shields.io/badge/Sendspin-Native%20Support-purple?style=for-the-badge)


## Key Features

- **Sendspin Protocol**: Native Music Assistant integration via SendSpin.SDK
- **Unlimited Players**: Create as many audio zones as you have outputs
- **Individual Volume Control**: Adjust each zone independently
- **Real-time Monitoring**: SignalR-based live status updates
- **Auto-Discovery**: Players automatically appear in Music Assistant
- **Persistent Config**: Survives container restarts and updates
- **Multi-Architecture**: Runs on AMD64 and ARM64 (Raspberry Pi)
- **REST API**: Full programmatic control with Swagger documentation
- **Health Monitoring**: Built-in container health checks at `/api/health`
- **Logging**: Comprehensive logging for troubleshooting
- **Home Assistant**: Native add-on for HAOS
- **12V Trigger Control**: Automatic amplifier power via USB relay boards


### Audio Support

- **USB DACs**: Automatic detection via PortAudio
- **Built-in Audio**: Support for motherboard audio outputs
- **HDMI Audio**: Multi-channel HDMI audio output support
- **Virtual Devices**: Null devices for testing, software defined ALSA devices

### Hardware Control

- **12V Triggers**: Automatic amplifier power control via USB relay boards
- Supports FTDI, USB HID, and Modbus (CH340) relay boards
- See [12V Trigger Guide](docs/12V-TRIGGERS.md) for setup


## Docker Hub Images

**Ready-to-deploy images available at**: `https://hub.docker.com/r/chrisuthe/squeezelitemultiroom`

| Tag         | Description             | Use Case               |
| ----------- | ----------------------- | ---------------------- |
| `latest`    | Latest stable release   | Production deployments |
| `X.Y.Z`     | Version-tagged release  | Pinned deployments     |
| `sha-XXXXX` | Commit-specific build   | Testing                |

### Quick Deployment

```bash
docker run -d \
  --name multiroom-audio \
  --network host \
  -p 8096:8096 \
  --device /dev/snd:/dev/snd \
  -v audio_config:/app/config \
  ghcr.io/chrisuthe/multiroom-audio:latest
```

Access web interface at `http://localhost:8096`

### Docker Compose

```yaml
services:
  multiroom-audio:
    image: ghcr.io/chrisuthe/multiroom-audio:latest
    container_name: multiroom-audio
    restart: unless-stopped
    network_mode: host
    ports:
      - "8096:8096"
    devices:
      - /dev/snd:/dev/snd
    volumes:
      - ./config:/app/config
      - ./logs:/app/logs
      # - /etc/asound.conf:/etc/asound.conf:ro  # Optional: custom ALSA config
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8096/api/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 40s
```

## Getting Started

### Prerequisites

- Docker environment (Linux recommended for audio)
- Music Assistant running
- Audio devices (USB DACs, built-in audio)

### Step 1: Deploy Container

Use Docker run or Docker Compose as shown above.

### Step 2: Access Web Interface

Navigate to `http://your-host-ip:8096`

### Step 3: Create Your First Player

1. Click **"Add Player"**
2. **Name**: Enter a descriptive name (e.g., "Living Room", "Kitchen")
3. **Audio Device**: Select from auto-detected PortAudio devices
4. Click **"Create Player"**

### Step 4: Start Playing

1. Click **"Start"** on your new player
2. Player appears in Music Assistant within 30-60 seconds
3. Begin streaming music to your multi-room setup!

## Home Assistant OS Add-on

For Home Assistant OS users, install as an add-on:

1. Add repository: `https://github.com/chrisuthe/squeezelite-docker`
2. Install "Multi-Room Audio Controller"
3. Start and access via sidebar

See [HAOS Add-on Guide](docs/HAOS_ADDON_GUIDE.md) for detailed instructions.

## Configuration Options

### Environment Variables

| Variable      | Default       | Description                                     |
| ------------- | ------------- | ----------------------------------------------- |
| `WEB_PORT`    | `8096`        | Web interface port                              |
| `LOG_LEVEL`   | `info`        | Logging verbosity (debug, info, warning, error) |
| `CONFIG_PATH` | `/app/config` | Configuration directory                         |
| `LOG_PATH`    | `/app/logs`   | Log directory                                   |

### Volume Mounts

```yaml
volumes:
  - ./config:/app/config               # Player configurations
  - ./logs:/app/logs                   # Application logs
  - /etc/asound.conf:/etc/asound.conf  # Required only if you need to access software defined devices 
```

### Audio Device Access

```yaml
devices:
  - /dev/snd:/dev/snd    # All audio devices (Linux)
```

### Custom ALSA Configuration

If you have custom ALSA configurations (virtual devices, software mixing, multi-channel routing, etc.), mount your `asound.conf` file to make those devices available inside the container:

```yaml
volumes:
  - ./config:/app/config
  - ./logs:/app/logs
  - /etc/asound.conf:/etc/asound.conf:ro   # Custom ALSA config
```

This is useful for:

- **Software mixing** (dmix) - Share audio devices between applications
- **Virtual devices** - Custom device aliases and routing
- **Multi-channel audio** - Configure surround sound or multi-output setups
- **Hardware-specific settings** - Device-specific sample rates, formats, or buffer sizes

Your custom ALSA devices will appear in the device list after the container restarts.

## REST API

Full API documentation available at `http://localhost:8096/docs` (Swagger UI).

### Quick Examples

```bash
# List all players
curl http://localhost:8096/api/players

# Create a Sendspin player
curl -X POST http://localhost:8096/api/players \
  -H "Content-Type: application/json" \
  -d '{"name": "Kitchen", "device": "0"}'

# Set volume
curl -X PUT http://localhost:8096/api/players/Kitchen/volume \
  -H "Content-Type: application/json" \
  -d '{"volume": 75}'

# Start/stop players
curl -X POST http://localhost:8096/api/players/Kitchen/restart
curl -X POST http://localhost:8096/api/players/Kitchen/stop

# Health check
curl http://localhost:8096/api/health
```

## Troubleshooting

### No Audio Devices Detected

**Linux**: Ensure audio devices are accessible

```bash
# Check available devices
aplay -l

# Verify device permissions
ls -la /dev/snd/
```

### Players Won't Start

1. **Check audio device availability**:

   ```bash
   docker exec multiroom-audio ls /dev/snd
   ```

2. **Test with null device**: Create player with device `null` for testing

3. **Review logs**:

   ```bash
   docker logs multiroom-audio
   ```

### Player Not Appearing in Music Assistant

- Wait 30-60 seconds for mDNS discovery
- Restart Music Assistant
- Ensure both containers are on the same network

## Technology Stack

| Component         | Technology                    |
| ----------------- | ----------------------------- |
| Runtime           | .NET 8.0 / ASP.NET Core       |
| Audio Protocol    | Sendspin via SendSpin.SDK 2.0 |
| Audio Output      | PortAudio via PortAudioSharp2 |
| Real-time Updates | SignalR                       |
| Configuration     | YAML via YamlDotNet           |
| API Documentation | Swagger/OpenAPI               |

## License and Credits

**License**: MIT License - see [LICENSE](LICENSE) file

**Credits**:

- **[SendSpin.SDK](https://github.com/Sendspin/spec)** - Sendspin protocol implementation
- **[Music Assistant](https://music-assistant.io/)** - Modern music library management and multi-room audio platform
- **[PortAudio](http://www.portaudio.com/)** - Cross-platform audio I/O library

For detailed license information, see [LICENSES.md](LICENSES.md).

## Support and Community

- **Issues**: Report bugs and request features via [GitHub Issues](https://github.com/chrisuthe/squeezelite-docker/issues)
- **Docker Hub**: Pre-built images at `https://hub.docker.com/r/chrisuthe/squeezelitemultiroom`
- **Documentation**: [Wiki](https://github.com/chrisuthe/squeezelite-docker/wiki)

## About This Project

This project was developed with the assistance of AI (Claude by Anthropic) via [Claude Code](https://claude.ai/code). A human provided direction, reviewed outputs, and made decisions, but the implementation was AI-assisted.

---
<!-- markdownlint-disable MD036 -->
<!-- markdownlint-disable MD033 -->
<div align="center">

**Transform your space into a connected audio experience**

*Built with .NET 8.0 for the open-source community*

[![Docker Hub](https://img.shields.io/badge/Docker%20Hub-chrisuthe%2Fsqueezelitemultiroom-blue?style=flat-square&logo=docker)](https://hub.docker.com/r/chrisuthe/squeezelitemultiroom)
[![GitHub](https://img.shields.io/badge/GitHub-Source%20Code-black?style=flat-square&logo=github)](https://github.com/chrisuthe/squeezelite-docker)

</div>
<!-- markdownlint-enable MD036 -->
<!-- markdownlint-enable MD033 -->
