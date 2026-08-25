# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-8526016

**Current Dev Build Changes** (recent)

- Advertise real DAC sample rates and bit depths to Music Assistant (#288)1
- Suspend and resume hardware sinks so silent cards recover (#281) (#287)
- Fix default.pa import losing sink descriptions with spaces (#286)
- Standardize remap sink naming and disambiguate duplicate sound cards (#283)
- chore: upgrade SendSpin.SDK 9.1.0 -> 9.2.0
- style: satisfy dotnet format on merged MQTT changes
- Merge dev into main for v5.2.2 release

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
