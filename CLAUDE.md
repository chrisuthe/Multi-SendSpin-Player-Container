# CLAUDE.md - AI Agent Configuration

> This file provides context for Claude Code and other AI agents working on this project.

## Project Overview

**Multi-Room Audio Docker Controller** - A Flask-based web application for managing multiple audio players with support for different backends (Squeezelite, Sendspin, Snapcast). Enables whole-home audio with USB DACs connected to a central server.

### Purpose

Transform a single Docker host with multiple USB audio devices into a multi-room audio system. Each audio zone gets its own player process that streams from Music Assistant, Logitech Media Server, or Snapcast Server.

### Key Users

- Home automation enthusiasts with multi-room audio setups
- Music Assistant and LMS users wanting additional audio endpoints
- Docker/NAS users looking for centralized audio management

## Architecture Quick Reference

```
Flask App (app.py)
    |
    +-- PlayerManager (orchestration)
    |       |
    |       +-- ConfigManager      -> /app/config/players.yaml
    |       +-- AudioManager       -> ALSA/PulseAudio devices
    |       +-- ProcessManager     -> Subprocess lifecycle
    |       +-- ProviderRegistry   -> Provider lookup
    |               |
    |               +-- SqueezeliteProvider
    |               +-- SendspinProvider
    |               +-- SnapcastProvider
    |
    +-- WebSocket (status updates every 2s)
    +-- REST API (/api/*)
    +-- Web UI (templates/index.html)
```

### Key Files to Understand First

1. `app/app.py` - Application entry point, PlayerManager class
2. `app/common.py` - All Flask routes, WebSocket handlers
3. `app/providers/base.py` - Provider abstraction interface
4. `app/managers/config_manager.py` - Configuration persistence
5. `app/environment.py` - Docker vs HAOS detection

## Development Commands

```bash
# Run tests
pytest tests/ -v

# Run tests with coverage
pytest tests/ --cov=app --cov-report=html

# Lint code
ruff check .

# Auto-fix lint issues
ruff check --fix .

# Format code
ruff format .

# Build Docker image
docker build -t squeezelite-multiroom .

# Run with no audio (development)
docker-compose -f docker-compose.no-audio.yml up --build

# Run with audio (Linux)
docker-compose up --build

# Build HAOS add-on locally
cd multiroom-audio
docker build --build-arg BUILD_FROM=ghcr.io/hassio-addons/base-python:18.0.0 -t multiroom-addon .
```

## Code Style Guidelines

### Python

- **Linter/Formatter**: Ruff (configured in pyproject.toml)
- **Type hints**: Use for all function signatures
- **Docstrings**: Google style, required for public methods
- **Line length**: 120 characters max
- **Imports**: Grouped (stdlib, third-party, local), sorted alphabetically

```python
# Example function style
def create_player(
    self,
    name: str,
    device: str,
    provider_type: str = "squeezelite",
    **extra_config: Any,
) -> tuple[bool, str]:
    """
    Create a new audio player.

    Args:
        name: Unique name for the player (used as identifier).
        device: Audio device ID (e.g., 'hw:0,0', 'null', 'default').
        provider_type: Provider type ('squeezelite', 'sendspin', 'snapcast').
        **extra_config: Additional provider-specific configuration.

    Returns:
        Tuple of (success: bool, message: str).
    """
```

### JavaScript

- Vanilla JS only (no frameworks)
- ES6+ features (const/let, arrow functions, template literals)
- Use `textContent` instead of `innerHTML` for security

### API Responses

```python
# Success response format
{"success": True, "message": "...", "data": {...}}

# Error response format
{"success": False, "error": "..."}
```

## Important Patterns

### Provider Pattern

All player backends implement `PlayerProvider` base class:

```python
class NewProvider(PlayerProvider):
    provider_type = "newprovider"
    display_name = "New Provider"
    binary_name = "provider-binary"

    def build_command(self, player, log_path) -> list[str]:
        # Return command to start player process
        pass

    def validate_config(self, config) -> tuple[bool, str]:
        # Validate provider-specific configuration
        pass
```

### Environment Detection

```python
from environment import is_hassio, get_audio_backend

if is_hassio():
    # HAOS-specific behavior (PulseAudio)
else:
    # Standalone Docker behavior (ALSA)
```

### Configuration Validation

Use Pydantic schemas in `app/schemas/player_config.py` for type-safe validation.

## Things to Avoid

1. **DO NOT** add external JavaScript frameworks - project uses vanilla JS only
2. **DO NOT** modify volume control to use provider-specific protocols - ALSA/amixer is intentionally used for consistency
3. **DO NOT** remove fallback support from Squeezelite provider - users rely on null device fallback
4. **DO NOT** change the default port from 8080 without updating all Docker configs
5. **DO NOT** use `yaml.load()` - always use `yaml.safe_load()` for security
6. **DO NOT** use shell=True in subprocess calls - use list-based commands
7. **DO NOT** commit hardcoded secrets - use environment variables

## Testing Guidelines

### Test File Naming

- `tests/test_<module_name>.py`
- Test functions: `test_<method_name>_<scenario>`

### Running Specific Tests

```bash
# Single test file
pytest tests/test_squeezelite_provider.py -v

# Single test function
pytest tests/test_api_endpoints.py::test_create_player_success -v

# Tests matching pattern
pytest tests/ -k "volume" -v
```

### Mock Patterns

```python
# Mock subprocess for ALSA commands
@patch("subprocess.run")
def test_volume_control(mock_run):
    mock_run.return_value = MagicMock(stdout="[75%]", returncode=0)
    ...
```

## Common Development Tasks

### Adding a New Provider

1. Create `app/providers/newplayer.py` implementing `PlayerProvider`
2. Register in `app/providers/__init__.py` exports
3. Register in `app/app.py` provider registry
4. Add Pydantic schema in `app/schemas/player_config.py`
5. Update `app/health_check.py` to check for the binary
6. Update Dockerfiles to install the binary
7. Add tests in `tests/test_newplayer_provider.py`

### Adding a New API Endpoint

1. Add route in `app/common.py` in `register_routes()`
2. Update `app/swagger.yaml` with OpenAPI spec
3. Add tests in `tests/test_api_endpoints.py`

### Modifying Player Configuration Schema

1. Update Pydantic model in `app/schemas/player_config.py`
2. Update provider's `validate_config()` and `prepare_config()`
3. Update `docs/ARCHITECTURE.md` if schema changes are significant

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `SQUEEZELITE_BUFFER_TIME` | `80` | ALSA buffer time (ms) |
| `SQUEEZELITE_BUFFER_PARAMS` | `500:2000` | Stream:output buffers (KB) |
| `SQUEEZELITE_WINDOWS_MODE` | `0` | Windows compatibility mode |
| `SECRET_KEY` | Auto-generated | Flask session security |
| `AUDIO_BACKEND` | Auto-detected | `alsa` or `pulse` |
| `CONFIG_PATH` | `/app/config` | Configuration directory |
| `LOG_PATH` | `/app/logs` | Log directory |

See `ENVIRONMENT_VARIABLES.md` for complete documentation.

## Project Structure Overview

```
squeezelite-docker/
├── app/                      # Main application
│   ├── app.py               # Entry point, PlayerManager
│   ├── common.py            # Routes, WebSocket, shared code
│   ├── environment.py       # Docker/HAOS detection
│   ├── health_check.py      # Container health verification
│   ├── managers/            # Business logic components
│   │   ├── audio_manager.py    # Device/volume control
│   │   ├── config_manager.py   # YAML persistence
│   │   └── process_manager.py  # Subprocess lifecycle
│   ├── providers/           # Player backend implementations
│   │   ├── base.py             # Abstract interface
│   │   ├── squeezelite.py      # Squeezelite provider
│   │   ├── sendspin.py         # Sendspin provider
│   │   └── snapcast.py         # Snapcast provider
│   ├── schemas/             # Pydantic validation
│   ├── templates/           # Jinja2 HTML templates
│   └── static/              # CSS/JS assets
├── multiroom-audio/         # Home Assistant OS add-on
│   ├── config.yaml          # Add-on metadata
│   ├── Dockerfile           # Alpine-based build
│   └── run.sh               # Startup script (bashio)
├── tests/                   # Pytest test suite
├── Dockerfile               # Production container (Debian)
├── Dockerfile.slim          # Slim variant (Sendspin only)
├── docker-compose.yml       # Main compose config
└── docker-compose.no-audio.yml  # Development without audio
```

## Useful Context

### Audio Device Formats

- **ALSA**: `hw:0,0`, `hw:1,3`, `plughw:0,0`
- **Virtual**: `null`, `pulse`, `dmix`, `default`
- **PortAudio**: Numeric index (`0`, `1`, `2`) for Sendspin

### Player Identification

- **Squeezelite**: Uses MAC address (auto-generated from player name hash)
- **Snapcast**: Uses host ID (auto-generated from player name hash)
- **Sendspin**: Uses player name directly

### Status Updates

WebSocket emits `status_update` event every 2 seconds with player running states.

## AI-Specific Notes

This project was developed with AI assistance. When making changes:

1. **Maintain consistency** with existing patterns
2. **Preserve backward compatibility** for existing configurations
3. **Update documentation** when changing behavior
4. **Add tests** for new functionality
5. **Consider both Docker and HAOS environments**

## Quick Links

- [Architecture Documentation](docs/ARCHITECTURE.md)
- [Contributing Guide](CONTRIBUTING.md)
- [Environment Variables](ENVIRONMENT_VARIABLES.md)
- [API Documentation](http://localhost:8080/api/docs) (when running)
