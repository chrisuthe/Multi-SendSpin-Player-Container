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
│   └── HealthEndpoint.cs        # /api/health
├── Services/
│   ├── PlayerManagerService.cs   # SDK player lifecycle
│   ├── ConfigurationService.cs   # YAML persistence
│   └── EnvironmentService.cs     # Docker vs HAOS detection
├── Audio/                        # Audio output layer
│   ├── BufferedAudioSampleSource.cs  # Bridges timed buffer to audio output
│   ├── PulseAudio/              # PulseAudio backend (primary)
│   └── Alsa/                    # ALSA backend (Docker fallback)
├── Utilities/
│   ├── ClientIdGenerator.cs     # MD5-based IDs
│   └── AlsaCommandRunner.cs     # Volume control
├── Models/                       # Request/Response types
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
| `SUPERVISOR_TOKEN` | (HAOS only) | Auto-set by Home Assistant supervisor |

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
├── nextgen.md                   # Implementation plan
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

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/players` | List all players |
| POST | `/api/players` | Create new player |
| GET | `/api/players/{name}` | Get player details |
| DELETE | `/api/players/{name}` | Delete player |
| POST | `/api/players/{name}/stop` | Stop player |
| POST | `/api/players/{name}/restart` | Restart player |
| PUT | `/api/players/{name}/volume` | Set volume |
| PUT | `/api/players/{name}/offset` | Set delay offset |
| GET | `/api/devices` | List audio devices |
| GET | `/api/providers` | List available providers |
| GET | `/api/cards` | List all sound cards |
| GET | `/api/cards/{id}` | Get card details |
| PUT | `/api/cards/{id}/profile` | Set card profile |
| PUT | `/api/cards/{id}/mute` | Mute/unmute card in real-time |
| PUT | `/api/cards/{id}/boot-mute` | Set boot mute preference |
| GET | `/api/health` | Health check |

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

## Quick Links

- [Implementation Plan](nextgen.md) - Original development roadmap
- [Home Assistant Add-on Docs](https://developers.home-assistant.io/docs/add-ons/configuration/)
- [API Documentation](http://localhost:8096/docs) (when running)
