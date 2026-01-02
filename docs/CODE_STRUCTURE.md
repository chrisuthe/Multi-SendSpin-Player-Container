# Code Structure Guide

This document provides a detailed walkthrough of the codebase for contributors and maintainers.

## Directory Overview

```
squeezelite-docker/
├── app/                          # Main Python application
│   ├── app.py                    # Application entry point
│   ├── common.py                 # Shared Flask routes and WebSocket handlers
│   ├── environment.py            # Environment detection (Docker/HAOS)
│   ├── env_validation.py         # Startup environment validation
│   ├── health_check.py           # Container health verification
│   ├── managers/                 # Business logic layer
│   │   ├── audio_manager.py      # ALSA device detection, volume control
│   │   ├── config_manager.py     # YAML configuration persistence
│   │   └── process_manager.py    # Subprocess lifecycle management
│   ├── providers/                # Player backend implementations
│   │   ├── base.py               # Abstract PlayerProvider interface
│   │   ├── registry.py           # Provider discovery and registration
│   │   ├── squeezelite.py        # Squeezelite (LMS) provider
│   │   ├── sendspin.py           # Sendspin (Music Assistant) provider
│   │   └── snapcast.py           # Snapcast provider
│   ├── schemas/                  # Configuration validation
│   │   └── player_config.py      # Pydantic models for player configs
│   ├── templates/                # Jinja2 HTML templates
│   │   └── index.html            # Main web UI
│   ├── static/                   # CSS and JavaScript
│   │   └── style.css             # Custom styling
│   └── swagger.yaml              # OpenAPI specification
├── multiroom-audio/              # Home Assistant OS add-on
│   ├── config.yaml               # Add-on metadata and options
│   ├── Dockerfile                # Alpine-based build
│   ├── run.sh                    # Startup script (bashio)
│   ├── DOCS.md                   # Add-on documentation
│   └── translations/             # Internationalization
├── tests/                        # Test suite
│   ├── conftest.py               # Pytest fixtures
│   └── test_*.py                 # Test modules
├── Dockerfile                    # Production container (Debian)
├── Dockerfile.slim               # Slim variant (Sendspin only)
├── docker-compose.yml            # Production compose
└── docker-compose.no-audio.yml   # Development without audio
```

## Core Application Flow

### Startup Sequence

```
1. app.py executes
   └── Configure logging
   └── Validate environment variables (env_validation.py)
   └── Create Flask app (common.create_flask_app)
   └── Initialize managers:
       ├── ConfigManager (loads players.yaml)
       ├── AudioManager (detects devices)
       └── ProcessManager (ready to manage subprocesses)
   └── Initialize ProviderRegistry with providers
   └── Create PlayerManager (orchestrates managers)
   └── Register routes (common.register_routes)
   └── Register WebSocket handlers (common.register_websocket_handlers)
   └── Start status monitor thread
   └── Run Flask-SocketIO server
```

### Request Flow

```
HTTP Request
    │
    ▼
Flask Route (common.py)
    │
    ▼
PlayerManager Method
    │
    ├──► ConfigManager (read/write players.yaml)
    ├──► ProviderRegistry (get provider for player type)
    │         │
    │         ▼
    │    Provider (build_command, validate_config, etc.)
    ├──► ProcessManager (start/stop subprocess)
    └──► AudioManager (volume control, device detection)
    │
    ▼
JSON Response
```

## Component Details

### PlayerManager (app/app.py)

The central orchestrator that coordinates all managers and providers.

**Key Methods:**
- `create_player()` - Creates new player with provider-specific validation
- `start_player()` - Starts player subprocess via ProcessManager
- `stop_player()` - Stops player subprocess
- `get_player_volume()` / `set_player_volume()` - Volume control via provider

### ConfigManager (app/managers/config_manager.py)

Handles configuration persistence with Pydantic validation.

**Key Features:**
- YAML file persistence (`/app/config/players.yaml`)
- Validation on load/save via Pydantic schemas
- Graceful handling of invalid configs (warns but continues)

### AudioManager (app/managers/audio_manager.py)

Manages audio device detection and volume control.

**Key Features:**
- ALSA device enumeration via `aplay -l`
- Volume control via `amixer` commands
- Test tone playback via `speaker-test` or `sounddevice`
- Fallback devices (null, default, dmix)

### ProcessManager (app/managers/process_manager.py)

Provider-agnostic subprocess lifecycle management.

**Key Features:**
- Process group management for clean termination
- Fallback command support
- Automatic dead process cleanup

### Provider System (app/providers/)

Pluggable backend architecture for different audio players.

**Base Interface (`base.py`):**
```python
class PlayerProvider(ABC):
    provider_type: str      # Unique identifier
    display_name: str       # Human-readable name
    binary_name: str        # Executable name

    def build_command(player, log_path) -> list[str]
    def build_fallback_command(player, log_path) -> list[str] | None
    def get_volume(player) -> int
    def set_volume(player, volume) -> tuple[bool, str]
    def validate_config(config) -> tuple[bool, str]
    def prepare_config(config) -> dict
```

**Implemented Providers:**

| Provider | Binary | Protocol | Volume Control |
|----------|--------|----------|----------------|
| SqueezeliteProvider | squeezelite | SlimProto | ALSA/amixer |
| SendspinProvider | sendspin | Native MA | ALSA/amixer |
| SnapcastProvider | snapclient | Snapcast | ALSA/amixer |

### Schema Validation (app/schemas/player_config.py)

Pydantic models for type-safe configuration validation.

**Key Schemas:**
- `BasePlayerConfig` - Common fields (name, device, volume, etc.)
- `SqueezelitePlayerConfig` - Squeezelite-specific fields (server_ip, mac_address)
- `SendspinPlayerConfig` - Sendspin-specific fields (server_url, client_id, delay_ms)

### Environment Detection (app/environment.py)

Detects runtime environment and configures appropriate backends.

**Detection Logic:**
```
if /data/options.json exists OR SUPERVISOR_TOKEN set:
    → HAOS environment (PulseAudio)
else:
    → Standalone Docker (ALSA)
```

## Web Interface

### Routes (app/common.py)

| Route | Method | Description |
|-------|--------|-------------|
| `/` | GET | Main web UI |
| `/api/players` | GET | List all players |
| `/api/players` | POST | Create player |
| `/api/players/<name>` | GET/PUT/DELETE | Player CRUD |
| `/api/players/<name>/start` | POST | Start player |
| `/api/players/<name>/stop` | POST | Stop player |
| `/api/players/<name>/volume` | GET/POST | Volume control |
| `/api/devices` | GET | List ALSA devices |
| `/api/devices/portaudio` | GET | List PortAudio devices |
| `/api/providers` | GET | List available providers |

### WebSocket Events

- `status_update` - Emitted every 2 seconds with player running states
- `connect` - Sends initial status on client connection

## Testing

### Test Structure

```
tests/
├── conftest.py                   # Fixtures (mock managers, temp files)
├── test_audio_manager.py         # AudioManager unit tests
├── test_config_manager.py        # ConfigManager unit tests
├── test_process_manager.py       # ProcessManager unit tests
├── test_squeezelite_provider.py  # Squeezelite provider tests
├── test_sendspin_provider.py     # Sendspin provider tests
├── test_snapcast_provider.py     # Snapcast provider tests
├── test_player_config_schema.py  # Pydantic schema tests
└── test_api_endpoints.py         # Flask route integration tests
```

### Running Tests

```bash
# All tests
pytest tests/ -v

# With coverage
pytest tests/ --cov=app --cov-report=html

# Single module
pytest tests/test_squeezelite_provider.py -v

# Pattern matching
pytest tests/ -k "volume" -v
```

## Home Assistant Add-on

### Add-on Structure (multiroom-audio/)

The add-on runs the same Python application but with:
- PulseAudio backend instead of ALSA
- Config stored in `/data` instead of `/app/config`
- Ingress-based web access (no direct port exposure)

### Key Differences from Standalone

| Aspect | Standalone Docker | HAOS Add-on |
|--------|-------------------|-------------|
| Audio Backend | ALSA | PulseAudio |
| Config Path | /app/config | /data |
| Log Path | /app/logs | /data/logs |
| Base Image | Debian | Alpine |
| Web Access | Direct port 8080 | HA Ingress |

## Extension Points

### Adding a New Provider

1. Create `app/providers/newplayer.py`:
```python
from .base import PlayerProvider, PlayerConfig

class NewPlayerProvider(PlayerProvider):
    provider_type = "newplayer"
    display_name = "New Player"
    binary_name = "newplayer-bin"

    def __init__(self, audio_manager):
        self.audio_manager = audio_manager

    def build_command(self, player, log_path):
        return [self.binary_name, "-n", player["name"], "-o", player["device"]]
    # ... implement other methods
```

2. Register in `app/providers/__init__.py`
3. Register in `app/app.py` provider registry
4. Add schema in `app/schemas/player_config.py`
5. Add tests in `tests/test_newplayer_provider.py`
6. Update Dockerfiles to install binary

### Adding a New API Endpoint

1. Add route in `register_routes()` in `app/common.py`
2. Add OpenAPI spec in `app/swagger.yaml`
3. Add tests in `tests/test_api_endpoints.py`
