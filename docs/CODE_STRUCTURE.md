# Code Structure Guide

This document provides a detailed walkthrough of the codebase for contributors and maintainers.

## Directory Overview

```
squeezelite-docker/
├── src/
│   └── MultiRoomAudio/               # Main C# application
│       ├── Audio/                    # Audio output layer
│       │   ├── BufferedAudioSampleSource.cs  # Bridges SDK buffer to player
│       │   ├── Alsa/
│       │   │   ├── AlsaPlayer.cs
│       │   │   └── AlsaDeviceEnumerator.cs
│       │   └── PulseAudio/           # Primary audio backend
│       │       ├── PulseAudioPlayer.cs
│       │       └── PulseAudioDeviceEnumerator.cs
│       ├── Controllers/              # REST API endpoints
│       │   ├── DevicesEndpoint.cs
│       │   ├── HealthEndpoint.cs
│       │   ├── PlayersEndpoint.cs
│       │   └── ProvidersEndpoint.cs
│       ├── Models/                   # Data models
│       │   ├── DeviceInfo.cs
│       │   ├── PlayerConfig.cs
│       │   └── PlayerStatus.cs
│       ├── Services/                 # Business logic
│       │   ├── ConfigurationService.cs
│       │   ├── EnvironmentService.cs
│       │   └── PlayerManagerService.cs
│       ├── Utilities/                # Helper classes
│       │   ├── AlsaCommandRunner.cs
│       │   └── ClientIdGenerator.cs
│       ├── wwwroot/                  # Static web UI
│       │   ├── index.html
│       │   ├── style.css
│       │   └── app.js
│       ├── Program.cs                # Application entry point
│       └── MultiRoomAudio.csproj     # Project file
├── docker/
│   └── Dockerfile                    # Production container build
├── multiroom-audio/                  # Home Assistant OS add-on
│   ├── config.yaml                   # Add-on metadata
│   ├── CHANGELOG.md                  # Version history
│   ├── DOCS.md                       # Add-on documentation
│   └── translations/
│       └── en.yaml                   # English strings
├── docs/                             # Documentation
└── squeezelite-docker.sln            # Visual Studio solution
```

## Core Application Flow

### Startup Sequence

```
1. Program.cs executes
   |
   +-- Configure logging based on LOG_LEVEL
   |
   +-- Create WebApplicationBuilder
   |
   +-- Register services (DI container):
   |   +-- EnvironmentService (singleton)
   |   +-- ConfigurationService (singleton)
   |   +-- PlayerManagerService (singleton)
   |
   +-- Configure middleware:
   |   +-- Static files (wwwroot)
   |   +-- Swagger/OpenAPI
   |   +-- SignalR hub
   |   +-- CORS
   |
   +-- Map endpoints (Controllers)
   |
   +-- Start Kestrel server on port 8096
   |
   +-- PlayerManagerService loads saved players
   |
   +-- AutoStart players begin playback
```

### Request Flow

```
HTTP Request
    |
    v
ASP.NET Core Middleware
    |
    v
Controller Endpoint
    |
    +---> PlayerManagerService
    |         |
    |         +---> SendSpin.SDK (player instances)
    |         |
    |         +---> ConfigurationService (persistence)
    |         |
    |         +---> PortAudioPlayer (audio output)
    |
    v
JSON Response
```

## Component Details

### Program.cs

The application entry point that configures and starts the ASP.NET Core host.

**Key responsibilities:**
- Configure dependency injection
- Set up middleware pipeline
- Map API endpoints
- Configure Kestrel server options

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddSingleton<EnvironmentService>();
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<PlayerManagerService>();

// Configure endpoints, middleware, etc.
var app = builder.Build();
app.Run();
```

### Controllers

#### PlayersEndpoint.cs

Main API for player management.

| Method | Route | Action |
|--------|-------|--------|
| GET | `/api/players` | List all players |
| POST | `/api/players` | Create player |
| GET | `/api/players/{name}` | Get player details |
| DELETE | `/api/players/{name}` | Delete player |
| POST | `/api/players/{name}/stop` | Stop player |
| POST | `/api/players/{name}/restart` | Restart player |
| PUT | `/api/players/{name}/volume` | Set volume |
| PUT | `/api/players/{name}/offset` | Set delay offset |

#### DevicesEndpoint.cs

Audio device enumeration.

| Method | Route | Action |
|--------|-------|--------|
| GET | `/api/devices` | List audio devices |

#### ProvidersEndpoint.cs

Provider information (Sendspin only in v2.0).

| Method | Route | Action |
|--------|-------|--------|
| GET | `/api/providers` | List available providers |

#### HealthEndpoint.cs

Container health check.

| Method | Route | Action |
|--------|-------|--------|
| GET | `/api/health` | Health status |

### Services

#### PlayerManagerService.cs

The central orchestrator for player lifecycle.

**Key methods:**
```csharp
// Lifecycle
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
```

**Internal state:**
- Dictionary of active players (name -> SDK player instance)
- Dictionary of player status (name -> PlayerStatus)

#### ConfigurationService.cs

YAML configuration persistence.

**Key methods:**
```csharp
Dictionary<string, PlayerConfig> LoadPlayers();
void SavePlayers(Dictionary<string, PlayerConfig> players);
void SavePlayer(PlayerConfig player);
void DeletePlayer(string name);
```

**Configuration file:** `players.yaml`

```yaml
Kitchen:
  name: Kitchen
  device: "0"
  serverIp: ""
  volume: 75
  delayOffsetMs: 0
  autoStart: true
```

#### EnvironmentService.cs

Runtime environment detection.

**Key properties:**
```csharp
bool IsHassio { get; }           // True if running in HAOS
string ConfigPath { get; }       // /app/config or /data
string LogPath { get; }          // /app/logs or /share/...
string AudioBackend { get; }     // alsa or pulse
```

**Detection logic:**
- Check for `/data/options.json` file
- Check for `SUPERVISOR_TOKEN` environment variable
- Either indicates HAOS mode

### Audio Layer

#### PortAudioPlayer.cs

Implements `IAudioPlayer` interface for SendSpin.SDK.

```csharp
public class PortAudioPlayer : IAudioPlayer
{
    public void Initialize(AudioFormat format);
    public void Write(ReadOnlySpan<byte> samples);
    public void SetVolume(float volume);
    public void Dispose();
}
```

**Responsibilities:**
- Initialize PortAudio output stream for specified device
- Buffer incoming audio samples
- Write samples to audio device
- Handle cleanup on dispose

#### PortAudioDeviceEnumerator.cs

Lists available PortAudio output devices.

```csharp
public class PortAudioDeviceEnumerator
{
    public IEnumerable<DeviceInfo> GetOutputDevices();
}
```

#### BufferedAudioSampleSource.cs

Manages audio sample buffering between SDK and PortAudio.

### Models

#### PlayerConfig.cs

Configuration for a player instance.

```csharp
public class PlayerConfig
{
    public string Name { get; set; }
    public string Device { get; set; }
    public string? ServerIp { get; set; }
    public int Volume { get; set; } = 75;
    public int DelayOffsetMs { get; set; } = 0;
    public bool AutoStart { get; set; } = false;
}
```

#### PlayerStatus.cs

Runtime status of a player.

```csharp
public class PlayerStatus
{
    public string Name { get; set; }
    public string Device { get; set; }
    public bool IsRunning { get; set; }
    public int Volume { get; set; }
    public int DelayOffsetMs { get; set; }
    public string? CurrentTrack { get; set; }
}
```

#### DeviceInfo.cs

Audio device information.

```csharp
public class DeviceInfo
{
    public int Index { get; set; }
    public string Name { get; set; }
    public int MaxOutputChannels { get; set; }
    public double DefaultSampleRate { get; set; }
}
```

### Utilities

#### ClientIdGenerator.cs

Generates deterministic client IDs for Sendspin protocol.

```csharp
public static class ClientIdGenerator
{
    public static string Generate(string playerName)
    {
        // MD5 hash of player name
        // Returns 12-character hex string
    }
}
```

#### AlsaCommandRunner.cs

Executes ALSA amixer commands for volume control.

```csharp
public class AlsaCommandRunner
{
    public Task<int> GetVolumeAsync(string device);
    public Task SetVolumeAsync(string device, int volume);
}
```

### Web Interface

The web UI is built with vanilla JavaScript (no frameworks).

**Files:**
- `wwwroot/index.html` - Main HTML structure
- `wwwroot/style.css` - Styling
- `wwwroot/app.js` - JavaScript logic

**Features:**
- Player list with status indicators
- Create player form
- Volume sliders
- Start/stop/delete buttons
- Real-time updates via SignalR

## Development Workflow

### Building

```bash
# Restore dependencies
dotnet restore src/MultiRoomAudio/MultiRoomAudio.csproj

# Build
dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj

# Run locally
dotnet run --project src/MultiRoomAudio/MultiRoomAudio.csproj
```

### Docker Build

```bash
# Build image
docker build -f docker/Dockerfile -t multiroom-audio .

# Run container
docker run -d -p 8096:8096 --device /dev/snd multiroom-audio
```

### Adding a New Feature

1. **Models**: Add/modify classes in `Models/`
2. **Services**: Update business logic in `Services/`
3. **Controllers**: Add API endpoints in `Controllers/`
4. **UI**: Update web interface in `wwwroot/`
5. **Config**: Update YAML schema if needed

### File Locations Summary

| Component | Location |
|-----------|----------|
| Entry point | `src/MultiRoomAudio/Program.cs` |
| API endpoints | `src/MultiRoomAudio/Controllers/` |
| Business logic | `src/MultiRoomAudio/Services/` |
| Audio handling | `src/MultiRoomAudio/Audio/` |
| Data models | `src/MultiRoomAudio/Models/` |
| Helpers | `src/MultiRoomAudio/Utilities/` |
| Web UI | `src/MultiRoomAudio/wwwroot/` |
| Docker build | `docker/Dockerfile` |
| HAOS metadata | `multiroom-audio/` |
