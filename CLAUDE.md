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
  - SendSpin.SDK v6.1.1 - Sendspin protocol handling

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
│   ├── FtdiRelayBoard.cs        # FTDI relay boards (Denkovi + generic)
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

## Testing Guidelines

When testing locally on macOS (where PulseAudio is not available):

1. **Always use mock hardware mode**: Run with `MOCK_HARDWARE=true` to get simulated audio devices

   ```bash
   MOCK_HARDWARE=true dotnet run --project src/MultiRoomAudio/MultiRoomAudio.csproj
   ```

2. **Check for existing instances**: Before starting the app, check if an old instance is running

   ```bash
   pgrep -f "MultiRoomAudio"
   ```

3. **Kill only MultiRoomAudio processes**: When stopping, target only the specific process

   ```bash
   pkill -f "MultiRoomAudio"
   ```

4. **Always run the app for user testing**: Before committing changes, start the app so the user can test interactively

5. **Verify process state changes**: When starting or stopping the app:
   - Wait **5 seconds** after starting before checking if it spawned
   - Wait **5 seconds** after killing before checking if it's gone
   - Use `pgrep -f "MultiRoomAudio"` to verify the process state
   - If the expected state isn't found, **retry the check once** before reporting failure

6. **Clear test data between runs**: Remove temporary config files created during testing

   ```bash
   rm -f src/MultiRoomAudio/config/sinks.yaml
   rm -f src/MultiRoomAudio/config/players.yaml  # if needed
   ```

7. **Test data locations** (local dev mode):
   - Config files: `src/MultiRoomAudio/config/`
   - Log files: `src/MultiRoomAudio/logs/`

---

## AI Agent Guidelines

1. **Remembering things**: When the user asks to "remember" something, add it to this CLAUDE.md file so it persists across sessions.

2. **UI changes and onboarding wizard**: When adding new UI elements to the main app, ask if the same change should also be applied to the onboarding wizard (`wwwroot/js/wizard.js`). Skip asking if explicitly told to add it there.

3. **Non-blocking startup**: Failure of player creation, sink creation, trigger detection, or any other startup phase must never block the user from reaching a useable UI. The `StartupOrchestrator` catches per-phase exceptions and marks them as `Failed` so subsequent phases still run and the web UI becomes interactive regardless.

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
| `ENABLE_ADVANCED_FORMATS` | `false` | Show format selection UI (dev-only). All players default to flac-48000 regardless. |

**Audio Configuration Notes:**
- `PA_SAMPLE_RATE` and `PA_SAMPLE_FORMAT` only apply in standalone Docker mode
- These settings are applied at container startup before PulseAudio starts
- PulseAudio will automatically negotiate down if hardware doesn't support the requested format
- Common formats: `s16le` (16-bit), `s24le` (24-bit), `s32le` (32-bit), `float32le` (32-bit float)
- Common rates: `44100`, `48000`, `96000`, `192000`

**Audio Format Selection:**

- All players default to advertising "flac-48000" for maximum Music Assistant compatibility
- This default applies regardless of `ENABLE_ADVANCED_FORMATS` setting
- When `ENABLE_ADVANCED_FORMATS=true`, UI shows dropdown to select specific formats or "All Formats"
- Format preference is persisted in players.yaml and survives restarts
- Changing format triggers automatic player restart to re-announce with new format

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
<PackageReference Include="SendSpin.SDK" Version="6.1.1" />
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
| PUT | `/api/players/{name}/auto-resume` | Enable/disable auto-resume on device reconnect |
| GET | `/api/players/formats` | Get available audio formats (only when ENABLE_ADVANCED_FORMATS=true) |

### Audio Devices

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/devices` | List audio devices |
| GET | `/api/devices/default` | Get default device (not used by UI) |
| GET | `/api/devices/aliases` | List device aliases (not used by UI - included in /api/devices) |
| GET | `/api/devices/{id}` | Get device details |
| GET | `/api/devices/{id}/capabilities` | Get device capabilities |
| POST | `/api/devices/refresh` | Refresh device list |
| POST | `/api/devices/rematch` | Rematch devices to players (not used by UI) |
| PUT | `/api/devices/{id}/alias` | Set device alias |
| PUT | `/api/devices/{id}/hidden` | Hide/unhide device |
| PUT | `/api/devices/{id}/max-volume` | Set device max volume |
| GET | `/api/devices/{id}/hid-status` | Get HID button status for a device |
| PUT | `/api/devices/{id}/hid-buttons` | Enable/disable HID button support |
| GET | `/api/providers` | List available providers (not used by UI) |

### Sound Cards

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/cards` | List all sound cards |
| GET | `/api/cards/saved` | List saved card configurations (not used by UI - included in /api/cards) |
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
| GET | `/api/sinks/import/status` | Get import status (not used by UI) |
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
| GET | `/api/logs/download` | Download all logs as a text file |
| DELETE | `/api/logs` | Clear logs |

### Health & Status

| Method | Endpoint | Description |
| ------ | -------- | ----------- |
| GET | `/api/health` | Health check (for orchestration, not UI) |
| GET | `/api/health/ready` | Ready check (for orchestration, not UI) |
| GET | `/api/health/live` | Liveness check (for orchestration, not UI) |
| GET | `/api/status` | System status (not used by UI - for monitoring tools) |

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

| Type             | VID:PID         | Supported Models                                    | Channel Detection |
|------------------|-----------------|-----------------------------------------------------|-------------------|
| **USB HID**      | `0x16C0:0x05DF` | DCT Tech, ucreatefun                                | Auto-detected     |
| **FTDI**         | `0x0403:0x6001` | Denkovi DAE-CB/Ro8-USB, DAE-CB/Ro4-USB, Generic 8ch | Manual (model)    |
| **Modbus/CH340** | `0x1A86:0x7523` | Sainsmart 16-channel                                | Manual            |
| **LCUS/CH340**   | `0x1A86:0x7523` | LCUS 1-8 channel relay boards                       | Auto-detected     |

### Board Identification

All board types use USB port path hash for consistent identification:

| Board Type | ID Format | Example |
|------------|-----------|---------|
| **HID** | `HID:8HEXCHARS` | `HID:CA88BCAC` |
| **FTDI** | `FTDI:8HEXCHARS` | `FTDI:7B9E3D1A` |
| **Modbus** | `MODBUS:8HEXCHARS` | `MODBUS:7F3A2B1C` |
| **LCUS** | `LCUS:8HEXCHARS` | `LCUS:2D4F6A8B` |

Board IDs are stable across reboots as long as the board stays in the same USB port. Moving a board to a different port will generate a new ID.

### HID Protocol Details

- Feature report byte 0: Report ID (0x00)
- Feature report bytes 1-5: Serial number (ASCII, may contain garbage - sanitized)
- Feature report byte 7: Relay state bitmask (unreliable - always returns 0x00 on some boards)
- Command `0xFF` + channel: Turn relay ON
- Command `0xFD` + channel: Turn relay OFF

### FTDI Protocol Details

- Uses **synchronous bitbang mode (0x04)** on FT245RL chip - NOT async bitbang (0x01)
- State written as single byte bitmask
- Requires `libftdi1` library
- Hardware state read-back supported via `ftdi_read_pins()`

**Pin Mapping:**

| Model                        | Relay 1 | Relay 2 | Relay 3 | Relay 4 | Relays 5-8 |
|------------------------------|---------|---------|---------|---------|------------|
| **Denkovi DAE-CB/Ro8-USB**   | Bit 0   | Bit 1   | Bit 2   | Bit 3   | Bits 4-7   |
| **Denkovi DAE-CB/Ro4-USB**   | Bit 1   | Bit 3   | Bit 5   | Bit 7   | N/A        |
| **Generic FTDI (8 channels)**| Bit 0   | Bit 1   | Bit 2   | Bit 3   | Bits 4-7   |

The Denkovi 4-channel board uses **odd pins only** (D1, D3, D5, D7), not sequential pins.

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

### LCUS/CH340 Protocol Details

- Uses simple binary protocol over serial (9600 baud, 8N1)
- USB-to-serial chip: CH340/CH341 (same as Modbus - detected by probing)
- **Status query**: Send `0xFF`, receive N bytes (one per channel, 0x00=OFF, 0x01=ON)
- **Channel count**: Auto-detected from status response (1-8 channels)
- **Command format**: `[0xA0][Channel][Operation][Checksum]`
  - Channel: 0x01-0x08 (1-based)
  - Operation: 0x00=OFF, 0x01=ON
  - Checksum: `(0xA0 + Channel + Operation) & 0xFF`
- **Example commands**:
  - Channel 1 ON: `A0 01 01 A2`
  - Channel 1 OFF: `A0 01 00 A1`
  - Channel 8 ON: `A0 08 01 A9`
- Requires `dialout` group membership for serial port access

### CH340 Device Detection

CH340-based boards (Modbus and LCUS) share the same USB VID:PID. During enumeration:

1. Probe with Modbus "read coils" command (`:FE0100000010F1\r\n`)
2. If no response, probe with LCUS status query (`0xFF`)
3. Devices that don't respond to either protocol are ignored (e.g., Arduino, GPS modules)

This prevents non-relay CH340 devices from appearing in the device list.

### Key Implementation Files

| File | Purpose |
| ---- | ------- |
| `src/MultiRoomAudio/Relay/IRelayBoard.cs` | Common interface for all relay board types |
| `src/MultiRoomAudio/Relay/HidRelayBoard.cs` | USB HID relay implementation using HidApi.Net |
| `src/MultiRoomAudio/Relay/FtdiRelayBoard.cs` | FTDI relay implementation using libftdi1 |
| `src/MultiRoomAudio/Relay/ModbusRelayBoard.cs` | Modbus ASCII relay implementation using System.IO.Ports |
| `src/MultiRoomAudio/Relay/LcusRelayBoard.cs` | LCUS binary relay implementation using System.IO.Ports |
| `src/MultiRoomAudio/Relay/Ch340RelayProbe.cs` | Unified CH340 device detection (Modbus vs LCUS) |
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
