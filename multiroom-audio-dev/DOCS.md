# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-2928c25

**Current Dev Build Changes** (recent)

- Merge pull request #232 from chrisuthe/fix/212-header-wrap
- Merge pull request #230 from chrisuthe/fix/223-dac-collision
- fix: prevent header title clipping with long translated titles
- fix: disambiguate identical USB devices that share a device key
- Merge pull request #228 from chrisuthe/fix/219-volume-scope
- Merge pull request #229 from chrisuthe/chore/dependabot-hold-swashbuckle-major
- ci: hold Swashbuckle major bumps until net9/net10 migration
- fix: only clamp hardware volume on player-assigned devices
- Merge pull request #217 from chrisuthe/dependabot/nuget/src/MultiRoomAudio/YamlDotNet-17.0.1
- Merge pull request #211 from chrisuthe/dependabot/nuget/src/MultiRoomAudio/System.IO.Ports-10.0.5

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
