# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-12692be

**Current Dev Build Changes** (recent)

- Merge pull request #237 from chrisuthe/fix/mqtt-dev-addon-options
- fix: expose MQTT options in dev add-on config
- Merge pull request #236 from chrisuthe/feat/mqtt-ha-bridge-phase2
- docs: reconcile virtual-board MQTT-down signal (LWT availability) in phase 2 spec
- refactor: early-return on no-op override release and add XML docs
- feat: add virtual board and override controls to triggers UI
- feat: expose MQTT options in HAOS add-on config
- fix: resolve HAOS MQTT options by snake_case keys
- feat: publish amp state and dispatch override commands in MqttService
- feat: parse amp override commands in MqttCommand

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
