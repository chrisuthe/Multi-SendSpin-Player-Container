# Changelog

## [1.2.6] - Fix PortAudio Segfault on HAOS

### Fixed
- **Segmentation fault on HAOS startup**: Made `sounddevice`/`numpy` imports lazy to avoid PortAudio C extension crash during module load
- PortAudio initialization on Alpine Linux (HAOS) can segfault before Python exception handling catches it
- Libraries are now only imported when `play_test_tone()` is actually called, not at app startup

---

## [1.2.5] - Fix HAOS Startup Crash

### Fixed
- **FileNotFoundError on HAOS startup**: App now correctly uses `LOG_PATH` and `CONFIG_PATH` environment variables instead of hardcoded `/app/logs` and `/app/config` paths
- Creates log directory before initializing file logging handler
- HAOS add-on sets these paths to `/data/logs` and `/data` which weren't being respected

---

## [1.2.4] - Simplify Add-on Config

### Changed
- Removed direct port exposure; now uses ingress only (sidebar access)
- Removed `default_server_ip` option; set server IP per-player in web UI instead
- Add-on configuration now only has `log_level` setting

---

## [1.2.2] - Startup Fix

### Fixed
- **Segmentation fault on startup**: Replaced gunicorn with Flask's built-in server to avoid fork-related crashes with SocketIO and audio libraries (sounddevice/PortAudio)
- The gunicorn pre-fork model caused segfaults when native audio libraries were initialized before the fork

### Technical Details
- Flask's development server is sufficient for home automation with limited concurrent users
- Eliminates race conditions between gunicorn worker forks and SocketIO/audio initialization

---

## [1.1.0] - Snapcast & HAOS Support

### Added
- Snapcast player provider for synchronized multiroom audio
- Home Assistant OS add-on support with PulseAudio integration
- Environment detection module for automatic Docker/HAOS adaptation
- Host ID auto-generation for Snapcast players
- Latency compensation configuration for Snapcast

### Changed
- Audio backend automatically switches between ALSA (Docker) and PulseAudio (HAOS)
- Updated documentation for all three player backends
- Improved provider architecture with consistent interfaces

### Notes
- Squeezelite and Snapcast use ALSA devices (`hw:X,Y`) in standalone Docker
- All providers use PulseAudio when running as HAOS add-on
- Sendspin continues to use PortAudio device indices

---

## [1.0.0] - Initial Release

### Added
- Multi-room audio player management via web interface
- Squeezelite player support (LMS/SlimProto protocol)
- Sendspin player support (Music Assistant native)
- Snapcast player support (synchronized multiroom)
- PulseAudio integration for HAOS audio system
- Ingress support for seamless HA sidebar integration
- Real-time player status via WebSocket
- Individual volume control per player
- Audio device auto-detection
- Persistent configuration across restarts

### Notes
- This is an experimental add-on
- Requires `full_access` permission for audio device access
- Uses Home Assistant's PulseAudio system (hassio_audio)
