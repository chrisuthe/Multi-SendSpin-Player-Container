# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-6df2449

**Current Dev Build Changes** (recent)

- Merge pull request #235 from chrisuthe/feat/mqtt-home-assistant-bridge
- fix: publish MQTT container ready state after startup completes
- feat: add MQTT settings API endpoint
- feat: wire MQTT bridge into startup orchestrator and DI
- fix: guard MqttService reconnect with shutdown flag
- fix: replace reconnect CTS per-disconnect and tidy MqttService
- feat: add MqttService connection, discovery, state, and command handling
- feat: raise PlayersChanged event alongside SignalR broadcasts
- feat: add MQTT command parser
- fix: use friendly HA entity names for container device and tighten test

> WARNING: This is a development build. For stable releases, use the stable add-on.
<!-- VERSION_INFO_END -->

---

## Warning

Development builds:
- May contain bugs or incomplete features
- Could have breaking changes between builds
- Are not recommended for production use

## Installation

This add-on is automatically updated whenever code is pushed to the `dev` branch.
The version number (sha-XXXXXXX) indicates the commit it was built from.

## Reporting Issues

When reporting issues with dev builds, please include:
- The commit SHA (visible in the add-on info)
- Steps to reproduce the issue
- Expected vs actual behavior

## Configuration

### Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `log_level` | string | `info` | Logging verbosity (debug, info, warning, error) |
| `mock_hardware` | bool | `false` | Enable mock audio devices and relay boards for testing without hardware |
| `enable_advanced_formats` | bool | `false` | Show format selection UI (players default to flac-48000 regardless) |

## For Stable Release

Use the "Multi-Room Audio Controller" add-on (without "Dev") for stable releases.
