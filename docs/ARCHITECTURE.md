# Multi-Room Audio Controller Architecture

This document describes the architecture of the C# ASP.NET Core 8.0 application for managing Sendspin audio players.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture Diagram](#architecture-diagram)
3. [Component Responsibilities](#component-responsibilities)
4. [Audio Pipeline](#audio-pipeline)
5. [Configuration Management](#configuration-management)
6. [Environment Detection](#environment-detection)
7. [API Reference](#api-reference)
8. [Technology Stack](#technology-stack)

---

## Overview

The Multi-Room Audio Controller is a C# ASP.NET Core 8.0 application that:

- Creates and manages Sendspin audio players
- Exposes a REST API for player management
- Provides a web UI for configuration
- Uses SignalR for real-time status updates
- Persists configuration in YAML format
- Runs in Docker or as a Home Assistant add-on

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| C# ASP.NET Core 8.0 | Modern, cross-platform, excellent performance |
| SendSpin.SDK | Native Sendspin protocol support with managed player lifecycle |
| PortAudioSharp2 | Cross-platform audio output |
| SignalR | Real-time player status without polling |
| YAML configuration | Human-readable, easy to edit manually |
| Single Docker image | Simplified deployment, no variant confusion |

---

## Architecture Diagram

```
                                    HTTP/WebSocket
                                          |
                                          v
    ┌─────────────────────────────────────────────────────────────────┐
    │                     ASP.NET Core 8.0 Host                        │
    │                        (Program.cs)                              │
    │                                                                  │
    │  ┌────────────────┐  ┌────────────────┐  ┌────────────────────┐ │
    │  │   Controllers  │  │   Static Files │  │   SignalR Hub      │ │
    │  │                │  │                │  │                    │ │
    │  │ - Players      │  │ - wwwroot/     │  │ - Status updates   │ │
    │  │ - Devices      │  │ - index.html   │  │ - Real-time sync   │ │
    │  │ - Providers    │  │ - style.css    │  │                    │ │
    │  │ - Health       │  │ - app.js       │  │                    │ │
    │  └───────┬────────┘  └────────────────┘  └────────────────────┘ │
    │          │                                                       │
    │          v                                                       │
    │  ┌────────────────────────────────────────────────────────────┐ │
    │  │                     Services Layer                          │ │
    │  │                                                              │ │
    │  │  ┌──────────────────┐  ┌──────────────────────────────────┐ │ │
    │  │  │ PlayerManager    │  │ ConfigurationService              │ │ │
    │  │  │ Service          │  │                                   │ │ │
    │  │  │                  │  │ - Load/save YAML                  │ │ │
    │  │  │ - Create player  │  │ - Player configs                  │ │ │
    │  │  │ - Start/stop     │  │ - Hot reload                      │ │ │
    │  │  │ - Volume control │  │                                   │ │ │
    │  │  │ - Status tracking│  └──────────────────────────────────┘ │ │
    │  │  └────────┬─────────┘                                        │ │
    │  │           │           ┌──────────────────────────────────┐  │ │
    │  │           │           │ EnvironmentService               │  │ │
    │  │           │           │                                   │  │ │
    │  │           │           │ - HAOS vs Docker detection        │  │ │
    │  │           │           │ - Config/log path resolution      │  │ │
    │  │           │           │ - Audio backend selection         │  │ │
    │  │           │           └──────────────────────────────────┘  │ │
    │  └───────────┼──────────────────────────────────────────────────┘ │
    │              │                                                    │
    │              v                                                    │
    │  ┌────────────────────────────────────────────────────────────┐  │
    │  │                      Audio Layer                            │  │
    │  │                                                              │  │
    │  │  ┌──────────────────────────────────────────────────────┐   │  │
    │  │  │                  SendSpin.SDK                         │   │  │
    │  │  │                                                        │   │  │
    │  │  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐   │   │  │
    │  │  │  │ Player 1    │  │ Player 2    │  │ Player N    │   │   │  │
    │  │  │  │ (Kitchen)   │  │ (Bedroom)   │  │ (...)       │   │   │  │
    │  │  │  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘   │   │  │
    │  │  └─────────┼────────────────┼────────────────┼──────────┘   │  │
    │  │            │                │                │               │  │
    │  │            v                v                v               │  │
    │  │  ┌──────────────────────────────────────────────────────┐   │  │
    │  │  │              PortAudioPlayer (IAudioPlayer)           │   │  │
    │  │  │                                                        │   │  │
    │  │  │  - PortAudio device enumeration                        │   │  │
    │  │  │  - Audio sample buffering                              │   │  │
    │  │  │  - Real-time audio output                              │   │  │
    │  │  └──────────────────────────────────────────────────────┘   │  │
    │  └──────────────────────────────────────────────────────────────┘  │
    └────────────────────────────────────────────────────────────────────┘
                                    │
                                    v
    ┌────────────────────────────────────────────────────────────────────┐
    │                        Native Audio                                 │
    │                                                                     │
    │    ALSA (Docker)              PulseAudio (HAOS)                    │
    │         │                           │                               │
    │         v                           v                               │
    │    USB DAC / Sound Card        hassio_audio                        │
    └────────────────────────────────────────────────────────────────────┘
```

---

## Component Responsibilities

### Controllers (REST API Endpoints)

| Controller | Path | Purpose |
|------------|------|---------|
| `PlayersEndpoint` | `/api/players` | CRUD operations, start/stop, volume |
| `DevicesEndpoint` | `/api/devices` | Audio device enumeration |
| `ProvidersEndpoint` | `/api/providers` | List available providers |
| `HealthEndpoint` | `/api/health` | Health check for container orchestration |

### Services

#### PlayerManagerService

The central orchestrator for player lifecycle management.

```csharp
public class PlayerManagerService
{
    // Player lifecycle
    Task<PlayerStatus> CreatePlayerAsync(PlayerConfig config);
    Task<bool> StartPlayerAsync(string name);
    Task<bool> StopPlayerAsync(string name);
    Task<bool> DeletePlayerAsync(string name);

    // Status
    IEnumerable<PlayerStatus> GetAllPlayers();
    PlayerStatus? GetPlayer(string name);

    // Control
    Task SetVolumeAsync(string name, int volume);
    Task SetDelayOffsetAsync(string name, int offsetMs);
}
```

**Responsibilities:**
- Creates SendSpin.SDK player instances
- Manages player lifecycle (start, stop, restart)
- Tracks player status and metadata
- Handles volume and delay offset control
- Coordinates with ConfigurationService for persistence

#### ConfigurationService

Handles YAML-based configuration persistence.

```csharp
public class ConfigurationService
{
    Dictionary<string, PlayerConfig> LoadPlayers();
    void SavePlayers(Dictionary<string, PlayerConfig> players);
    void SavePlayer(PlayerConfig player);
    void DeletePlayer(string name);
}
```

**Responsibilities:**
- Load/save player configurations from YAML
- Automatic directory creation
- Hot reload support

#### EnvironmentService

Detects runtime environment and provides appropriate paths.

```csharp
public class EnvironmentService
{
    bool IsHassio { get; }
    string ConfigPath { get; }
    string LogPath { get; }
    string AudioBackend { get; }
}
```

**Detection Logic:**
```
if /data/options.json exists OR SUPERVISOR_TOKEN set:
    HAOS mode (PulseAudio, /data config)
else:
    Docker mode (ALSA, /app/config)
```

### Audio Layer

#### PortAudioPlayer

Implements `IAudioPlayer` interface for SendSpin.SDK.

```csharp
public class PortAudioPlayer : IAudioPlayer
{
    void Initialize(AudioFormat format);
    void Write(ReadOnlySpan<byte> samples);
    void SetVolume(float volume);
    void Dispose();
}
```

**Responsibilities:**
- Initialize PortAudio output stream
- Buffer and write audio samples
- Handle sample rate conversion if needed
- Graceful cleanup on dispose

#### PortAudioDeviceEnumerator

Lists available audio output devices.

```csharp
public class PortAudioDeviceEnumerator
{
    IEnumerable<DeviceInfo> GetOutputDevices();
}
```

### Utilities

#### ClientIdGenerator

Generates deterministic client IDs for Sendspin protocol.

```csharp
public static class ClientIdGenerator
{
    string Generate(string playerName);  // MD5-based
}
```

#### AlsaCommandRunner

Executes ALSA commands for volume control.

```csharp
public class AlsaCommandRunner
{
    Task<int> GetVolumeAsync(string device);
    Task SetVolumeAsync(string device, int volume);
}
```

---

## Audio Pipeline

```
Music Assistant
      |
      | Sendspin Protocol (mDNS discovery, audio streaming)
      v
+-------------------------------------------------------------+
| SendSpin.SDK                                                 |
|                                                              |
| - Protocol impl                                              |
| - TimedAudioBuffer (buffering + sync timing + rate adj)     |
| - Clock synchronization                                      |
+---------------------------+----------------------------------+
                            | Sync-adjusted PCM samples
                            v
+-------------------------------------------------------------+
| BufferedAudioSampleSource                                    |
|                                                              |
| - Direct passthrough (no resampling)                        |
| - Bridges SDK buffer to audio player                        |
+---------------------------+----------------------------------+
                            | PCM Float32 (source rate)
                            v
+-------------------------------------------------------------+
| PulseAudioPlayer                                             |
|                                                              |
| - Sample format conversion (float -> S32_LE/S24_LE/S16_LE)  |
| - Volume control                                             |
| - PulseAudio output via pa_simple API                       |
+---------------------------+----------------------------------+
                            |
                            v
+-------------------------------------------------------------+
| PulseAudio Server                                            |
|                                                              |
| - Sample rate conversion to device native rate              |
| - Format negotiation with device                            |
+---------------------------+----------------------------------+
                            |
                            v
                   USB DAC / Sound Card
```

For detailed audio pipeline documentation, see [AUDIO_PIPELINE.md](AUDIO_PIPELINE.md).

---

## Configuration Management

### Player Configuration Schema

```yaml
# /app/config/players.yaml (Docker)
# /data/players.yaml (HAOS)

Kitchen:
  name: Kitchen
  device: "0"
  serverIp: ""
  volume: 75
  delayOffsetMs: 0
  autoStart: true

Bedroom:
  name: Bedroom
  device: "1"
  serverIp: ""
  volume: 80
  delayOffsetMs: 50
  autoStart: false
```

### Configuration Flow

```
Startup:
  1. EnvironmentService determines config path
  2. ConfigurationService loads players.yaml
  3. PlayerManagerService creates player instances
  4. AutoStart players begin playback

Runtime:
  1. API request to create/modify player
  2. PlayerManagerService updates state
  3. ConfigurationService persists to YAML
  4. SignalR broadcasts status update
```

---

## Environment Detection

| Environment | Detection Method | Config Path | Audio Backend |
|-------------|------------------|-------------|---------------|
| Docker | Default | `/app/config` | ALSA |
| HAOS | `/data/options.json` exists | `/data` | PulseAudio |
| HAOS | `SUPERVISOR_TOKEN` set | `/data` | PulseAudio |

---

## API Reference

### Core Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/players` | List all players with status |
| `POST` | `/api/players` | Create new player |
| `GET` | `/api/players/{name}` | Get player details |
| `DELETE` | `/api/players/{name}` | Delete player |
| `POST` | `/api/players/{name}/stop` | Stop player |
| `POST` | `/api/players/{name}/restart` | Restart player |
| `PUT` | `/api/players/{name}/volume` | Set volume (0-100) |
| `PUT` | `/api/players/{name}/offset` | Set delay offset (ms) |
| `GET` | `/api/devices` | List audio devices |
| `GET` | `/api/providers` | List available providers |
| `GET` | `/api/health` | Health check |

### Response Format

```json
{
  "success": true,
  "message": "Player created",
  "data": { ... }
}
```

---

## Technology Stack

### Runtime

| Component | Version | Purpose |
|-----------|---------|---------|
| .NET | 8.0 | Runtime framework |
| ASP.NET Core | 8.0 | Web framework |
| Kestrel | (built-in) | HTTP server |

### NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| SendSpin.SDK | 2.0.0 | Sendspin protocol |
| PortAudioSharp2 | 1.0.2 | Audio output |
| YamlDotNet | 16.2.1 | Configuration |
| Microsoft.AspNetCore.SignalR | 1.1.0 | Real-time updates |
| Swashbuckle.AspNetCore | 6.5.0 | API documentation |

### Deployment

| Target | Base Image | Size |
|--------|------------|------|
| Docker | Alpine Linux | ~80MB |
| HAOS | Alpine Linux | ~80MB |

---

## Future Considerations

### Potential Enhancements

- **Player Groups**: Synchronized multi-room playback
- **Audio DSP**: EQ, crossfade, room correction
- **WebSocket Streaming**: Browser-based playback
- **Metrics**: Prometheus/Grafana integration
