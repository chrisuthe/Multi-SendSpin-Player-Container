# CLAUDE.md - AI Agent Configuration

> This file provides context for Claude Code and other AI agents working on this project.

## Project Overview

**Multi-Room Audio Controller** - A C# ASP.NET Core 8.0 application for managing Sendspin audio players. Enables whole-home audio with USB DACs connected to a central server.

### Purpose

Transform a single Docker host with multiple USB audio devices into a multi-room audio system. Each audio zone gets its own player that streams from Music Assistant via the Sendspin protocol.

### Key Users

- Home automation enthusiasts with multi-room audio setups
- Music Assistant users wanting additional audio endpoints
- Docker/NAS users looking for centralized audio management

---

## Reference Documentation

- **Home Assistant Add-on Development:**
  - [Add-on Configuration](https://developers.home-assistant.io/docs/add-ons/configuration/) - config.yaml schema
  - [Add-on Communication](https://developers.home-assistant.io/docs/add-ons/communication/) - Ingress, supervisor
  - [Add-on Publishing](https://developers.home-assistant.io/docs/add-ons/publishing/) - Repository setup
- **Reference Add-ons:**
  - [home-assistant/addons/vlc](https://github.com/home-assistant/addons/tree/master/vlc) - Official VLC add-on (audio player pattern)
- **SDK Documentation:**
  - SendSpin.SDK v5.2.0 - Sendspin protocol handling

---

## Architecture

```
ASP.NET Core 8.0 Application
├── Controllers/                  # REST API endpoints
│   ├── PlayersEndpoint.cs       # /api/players CRUD
│   ├── DevicesEndpoint.cs       # /api/devices
│   ├── ProvidersEndpoint.cs     # /api/providers
│   ├── TriggersEndpoint.cs      # /api/triggers (12V relay control)
│   └── HealthEndpoint.cs        # /api/health
├── Services/
│   ├── PlayerManagerService.cs   # SDK player lifecycle
│   ├── ConfigurationService.cs   # YAML persistence
│   ├── TriggerService.cs        # Relay board management, player↔relay mapping
│   └── EnvironmentService.cs     # Docker vs HAOS detection
├── Relay/                        # 12V trigger hardware abstraction
│   ├── IRelayBoard.cs           # Common relay board interface
│   ├── HidRelayBoard.cs         # USB HID relay boards (DCT Tech)
│   ├── FtdiRelayBoard.cs        # FTDI relay boards (Denkovi)
│   └── MockRelayBoard.cs        # Mock board for testing
├── Audio/                        # Audio output layer
│   ├── BufferedAudioSampleSource.cs  # Bridges timed buffer to audio output
│   ├── PulseAudio/              # PulseAudio backend (primary)
│   └── Alsa/                    # ALSA backend (Docker fallback)
├── Utilities/
│   ├── ClientIdGenerator.cs     # MD5-based IDs
│   └── AlsaCommandRunner.cs     # Volume control
├── Models/
│   ├── TriggerModels.cs         # Trigger/relay data models
│   └── ...                      # Other request/response types
├── wwwroot/                      # Static web UI
└── Program.cs                    # Entry point
```

### Key Files to Understand First

1. `src/MultiRoomAudio/Program.cs` - Entry point, DI setup
2. `src/MultiRoomAudio/Services/PlayerManagerService.cs` - Core player management
3. `src/MultiRoomAudio/Services/ConfigurationService.cs` - YAML config persistence
4. `src/MultiRoomAudio/Services/EnvironmentService.cs` - HAOS vs Docker detection

---

## Development Commands

```bash
# Restore dependencies
dotnet restore src/MultiRoomAudio/MultiRoomAudio.csproj

# Build project
dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj

# Run locally (Windows/macOS - audio won't work)
dotnet run --project src/MultiRoomAudio/MultiRoomAudio.csproj

# Build Docker image
docker build -f docker/Dockerfile -t multiroom-audio .

# Run with Docker (Linux with audio)
docker run -d --name multiroom \
  -p 8096:8096 \
  --device /dev/snd \
  -v $(pwd)/config:/app/config \
  multiroom-audio

# Build for HAOS
docker build -f docker/Dockerfile \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/chrisuthe/multiroom-audio-hassio .
```

---

## Code Style Guidelines

### C#

- **Target Framework**: .NET 8.0
- **Style**: Follow Microsoft C# conventions
- **Nullable**: Enabled project-wide
- **Documentation**: XML doc comments for public APIs

```csharp
/// <summary>
/// Creates and starts a new audio player.
/// </summary>
/// <param name="request">Player configuration request.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The created player response.</returns>
public async Task<PlayerResponse> CreatePlayerAsync(
    PlayerCreateRequest request,
    CancellationToken ct = default)
{
    // Implementation
}
```

### JavaScript

- Vanilla JS only (no frameworks)
- ES6+ features (const/let, arrow functions, template literals)
- Use `textContent` instead of `innerHTML` for XSS prevention

### API Responses

```csharp
// Success response
new { success = true, message = "...", data = ... }

// Error response
new ErrorResponse(false, "Error message")
```

---

## Things to Avoid

1. **DO NOT** add external JavaScript frameworks - project uses vanilla JS only
2. **DO NOT** change the default port from 8096 without updating all configs
3. **DO NOT** commit hardcoded secrets - use environment variables
4. **DO NOT** manually edit `multiroom-audio/config.yaml` version - CI auto-updates it
5. **DO NOT** enable trimming in publish - SendSpin.SDK uses reflection

---

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `WEB_PORT` | `8096` | Web server port |
| `LOG_LEVEL` | `info` | Logging level (debug, info, warning, error) |
| `AUDIO_BACKEND` | Auto-detected | `alsa` or `pulse` |
| `CONFIG_PATH` | `/app/config` | Configuration directory (Docker mode) |
| `LOG_PATH` | `/app/logs` | Log directory (Docker mode) |
| `PA_SAMPLE_RATE` | `48000` | PulseAudio sample rate (Docker mode only) |
| `PA_SAMPLE_FORMAT` | `float32le` | PulseAudio sample format (Docker mode only) |
| `SUPERVISOR_TOKEN` | (HAOS only) | Auto-set by Home Assistant supervisor |
| `MOCK_HARDWARE` | `false` | Enable mock relay boards for testing without hardware |
| `ENABLE_ADVANCED_FORMATS` | `false` | Enable per-player format selection (dev-only feature) |

**Audio Configuration Notes:**
- `PA_SAMPLE_RATE` and `PA_SAMPLE_FORMAT` only apply in standalone Docker mode
- These settings are applied at container startup before PulseAudio starts
- PulseAudio will automatically negotiate down if hardware doesn't support the requested format
- Common formats: `s16le` (16-bit), `s24le` (24-bit), `s32le` (32-bit), `float32le` (32-bit float)
- Common rates: `44100`, `48000`, `96000`, `192000`

---

## Project Structure

```
squeezelite-docker/
├── src/
│   └── MultiRoomAudio/          # Main C# application
│       ├── Audio/               # PortAudio integration
│       ├── Controllers/         # REST API endpoints
│       ├── Models/              # Data models
│       ├── Services/            # Business logic
│       ├── Utilities/           # Helpers
│       ├── wwwroot/             # Static web UI
│       ├── Program.cs           # Entry point
│       └── MultiRoomAudio.csproj
├── docker/
│   └── Dockerfile               # Unified Alpine image
├── multiroom-audio/             # HAOS add-on metadata
│   ├── config.yaml              # Add-on config
│   ├── CHANGELOG.md
│   └── DOCS.md
├── CLAUDE.md                    # This file
└── squeezelite-docker.sln       # Solution file
```

---

## NuGet Packages

```xml
<PackageReference Include="SendSpin.SDK" Version="5.2.0" />
<PackageReference Include="YamlDotNet" Version="16.3.0" />
<PackageReference Include="Microsoft.AspNetCore.SignalR" Version="1.2.0" />
<PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="8.0.22" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="8.1.4" />
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.1" />
```

---

## API Endpoints

### Players

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/players` | List all players |
| POST | `/api/players` | Create new player |
| GET | `/api/players/{name}` | Get player details |
| GET | `/api/players/{name}/stats` | Get player statistics |
| PUT | `/api/players/{name}` | Update player settings |
| PUT | `/api/players/{name}/rename` | Rename player |
| DELETE | `/api/players/{name}` | Delete player |
| POST | `/api/players/{name}/start` | Start player |
| POST | `/api/players/{name}/stop` | Stop player |
| POST | `/api/players/{name}/restart` | Restart player |
| POST | `/api/players/{name}/pause` | Pause player |
| POST | `/api/players/{name}/resume` | Resume player |
| PUT | `/api/players/{name}/device` | Change player device |
| PUT | `/api/players/{name}/volume` | Set volume |
| PUT | `/api/players/{name}/startup-volume` | Set startup volume |
| PUT | `/api/players/{name}/mute` | Mute/unmute player |
| PUT | `/api/players/{name}/offset` | Set delay offset |

### Audio Devices

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/devices` | List audio devices |
| GET | `/api/devices/default` | Get default device |
| GET | `/api/devices/aliases` | List device aliases |
| GET | `/api/devices/{id}` | Get device details |
| GET | `/api/devices/{id}/capabilities` | Get device capabilities |
| POST | `/api/devices/refresh` | Refresh device list |
| POST | `/api/devices/rematch` | Rematch devices to players |
| PUT | `/api/devices/{id}/alias` | Set device alias |
| PUT | `/api/devices/{id}/hidden` | Hide/unhide device |
| PUT | `/api/devices/{id}/max-volume` | Set device max volume |
| GET | `/api/providers` | List available providers |

### Sound Cards

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/cards` | List all sound cards |
| GET | `/api/cards/saved` | List saved card configurations |
| GET | `/api/cards/{id}` | Get card details |
| PUT | `/api/cards/{id}/profile` | Set card profile |
| PUT | `/api/cards/{id}/mute` | Mute/unmute card in real-time |
| PUT | `/api/cards/{id}/boot-mute` | Set boot mute preference |
| PUT | `/api/cards/{id}/max-volume` | Set card max volume |
| DELETE | `/api/cards/{id}/saved` | Delete saved card config |

### Custom Sinks

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sinks` | List custom audio sinks |
| GET | `/api/sinks/channels` | List available channel mappings |
| GET | `/api/sinks/{name}` | Get sink details |
| GET | `/api/sinks/{name}/status` | Get sink status |
| POST | `/api/sinks/combine` | Create combined sink |
| POST | `/api/sinks/remap` | Create remapped sink |
| POST | `/api/sinks/{name}/reload` | Reload sink |
| POST | `/api/sinks/{name}/test-tone` | Play test tone |
| DELETE | `/api/sinks/{name}` | Delete custom sink |
| GET | `/api/sinks/import/scan` | Scan for importable sinks |
| GET | `/api/sinks/import/status` | Get import status |
| POST | `/api/sinks/import` | Import sinks from default.pa |

### 12V Triggers (Relay Control)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/triggers` | Get trigger feature status and all boards |
| PUT | `/api/triggers/enabled` | Enable/disable the trigger feature |
| GET | `/api/triggers/devices` | List available FTDI devices (legacy) |
| GET | `/api/triggers/devices/all` | List all relay devices (FTDI + HID) |
| GET | `/api/triggers/boards` | List all configured boards |
| POST | `/api/triggers/boards` | Add a new relay board |
| GET | `/api/triggers/boards/{boardId}` | Get specific board status |
| PUT | `/api/triggers/boards/{boardId}` | Update board settings |
| DELETE | `/api/triggers/boards/{boardId}` | Remove a board |
| POST | `/api/triggers/boards/{boardId}/reconnect` | Reconnect a specific board |
| GET | `/api/triggers/boards/{boardId}/{channel}` | Get channel status |
| PUT | `/api/triggers/boards/{boardId}/{channel}` | Configure trigger channel |
| DELETE | `/api/triggers/boards/{boardId}/{channel}` | Unassign trigger channel |
| POST | `/api/triggers/boards/{boardId}/{channel}/test` | Test relay (on/off) |

### Onboarding

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/onboarding/status` | Get onboarding status |
| POST | `/api/onboarding/complete` | Mark onboarding complete |
| POST | `/api/onboarding/skip` | Skip onboarding |
| POST | `/api/onboarding/reset` | Reset onboarding |
| POST | `/api/onboarding/create-players` | Create players from onboarding |
| POST | `/api/devices/{id}/test-tone` | Play test tone on device |

### Logs

| Method | Endpoint | Description |
| ------ | -------- | ----------- |
| GET | `/api/logs` | Get log entries |
| GET | `/api/logs/stats` | Get log statistics |
| DELETE | `/api/logs` | Clear logs |

### Health & Status

| Method | Endpoint | Description |
| ------ | -------- | ----------- |
| GET | `/api/health` | Health check |
| GET | `/api/health/ready` | Ready check |
| GET | `/api/health/live` | Liveness check |
| GET | `/api/status` | System status |

---

## HAOS vs Docker Detection

The `EnvironmentService` automatically detects the runtime environment:

- **HAOS Mode**: Detected via `/data/options.json` or `SUPERVISOR_TOKEN`
  - Config path: `/data`
  - Log path: `/share/multiroom-audio/logs`
  - Audio backend: PulseAudio

- **Docker Mode**: Default fallback
  - Config path: `/app/config` (or `CONFIG_PATH` env)
  - Log path: `/app/logs` (or `LOG_PATH` env)
  - Audio backend: ALSA

---

## 12V Trigger Hardware Reference

The trigger system supports USB relay boards for automatic amplifier power control.

### Supported Hardware

| Type | VID:PID | Example Products | Channel Detection |
| ---- | ------- | ---------------- | ----------------- |
| **USB HID** | `0x16C0:0x05DF` | DCT Tech, ucreatefun | Auto-detected from product name (e.g., "USBRelay8") |
| **FTDI** | `0x0403:0x6001` | Denkovi DAE0006K | Manual configuration required |
| **Modbus/CH340** | `0x1A86:0x7523` | Sainsmart 16-channel | Manual configuration required |

### Board Identification

Boards are identified in priority order:

1. **Serial Number** (preferred) - Stable across reboots and USB port changes
2. **USB Port Path** (fallback) - For boards without unique serial numbers, format: `USB:1-2.3`
3. **Serial Port** (Modbus boards) - Format: `MODBUS:/dev/ttyUSB0`

### HID Protocol Details

- Feature report byte 0: Report ID (0x00)
- Feature report bytes 1-5: Serial number (ASCII, may contain garbage - sanitized)
- Feature report byte 7: Relay state bitmask (unreliable - always returns 0x00 on some boards)
- Command `0xFF` + channel: Turn relay ON
- Command `0xFD` + channel: Turn relay OFF

### FTDI Protocol Details

- Uses bitbang mode on FT245RL chip
- State written as single byte bitmask (bit 0 = channel 1, etc.)
- Requires `libftdi1` library

### Modbus/CH340 Protocol Details

- Uses Modbus ASCII protocol over serial (9600 baud, 8N1)
- USB-to-serial chip: CH340/CH341 (appears as `/dev/ttyUSB*` on Linux)
- Device address: 0xFE (254)
- Function code 0x05: Write single coil
- Relay addresses: 0x00-0x0F for channels 1-16
- ON value: 0xFF00, OFF value: 0x0000
- Command format: `:FE05XXXXYYYYCC\r\n` where CC is LRC checksum
- Board echoes commands back as acknowledgment (no separate response)
- Requires `dialout` group membership for serial port access

### Key Implementation Files

| File | Purpose |
| ---- | ------- |
| `src/MultiRoomAudio/Relay/IRelayBoard.cs` | Common interface for all relay board types |
| `src/MultiRoomAudio/Relay/HidRelayBoard.cs` | USB HID relay implementation using HidApi.Net |
| `src/MultiRoomAudio/Relay/FtdiRelayBoard.cs` | FTDI relay implementation using libftdi1 |
| `src/MultiRoomAudio/Relay/ModbusRelayBoard.cs` | Modbus ASCII relay implementation using System.IO.Ports |
| `src/MultiRoomAudio/Relay/MockRelayBoard.cs` | Mock implementation for testing |
| `src/MultiRoomAudio/Services/TriggerService.cs` | Multi-board management, player↔channel mapping |
| `src/MultiRoomAudio/Models/TriggerModels.cs` | Data models, enums, request/response types |

### Startup/Shutdown Behaviors

| Behavior | Description |
| -------- | ----------- |
| `AllOff` | Turn all relays OFF (default - safest) |
| `AllOn` | Turn all relays ON |
| `NoChange` | Preserve current hardware state |

### Testing Without Hardware

Set `MOCK_HARDWARE=true` to enable mock relay boards that simulate real hardware behavior.

---

## Quick Links

- [Home Assistant Add-on Docs](https://developers.home-assistant.io/docs/add-ons/configuration/)
- [API Documentation](http://localhost:8096/docs) (when running)
