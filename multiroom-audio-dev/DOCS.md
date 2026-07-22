# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-31fe54e

**Current Dev Build Changes** (recent)

- fix: stop a cleared off-delay box from failing trigger sink saves (#250) (#268)
- fix: stop republishing unchanged retained MQTT topics (#256) (#269)
- feat: allow a relay trigger to reference multiple sinks (#250) (#255)
- feat: improve MQTT discovery for HA automations (#249) (#254)
- fix: stop reporting benign startup sample discard as a buffer overflow (#233) (#253)
- Merge pull request #248 from chrisuthe/fix/combine-sink-resume
- fix: load custom sinks in dependency order so combine-of-remaps survives restart
- docs: add 5.2.0 changelog (MQTT bridge, amp overrides, SDK 9.1.0)
- Merge pull request #239 from chrisuthe/fix/mqtt-unique-client-id
- fix: use a unique MQTT client ID to stop broker takeover loop

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
