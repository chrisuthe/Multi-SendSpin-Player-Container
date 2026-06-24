# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-d3b8dfb

**Current Dev Build Changes** (recent)

- Merge pull request #217 from chrisuthe/dependabot/nuget/src/MultiRoomAudio/YamlDotNet-17.0.1
- Merge pull request #211 from chrisuthe/dependabot/nuget/src/MultiRoomAudio/System.IO.Ports-10.0.5
- Merge pull request #209 from chrisuthe/dependabot/nuget/src/MultiRoomAudio/Microsoft.Extensions.Diagnostics.HealthChecks-10.0.5
- Merge pull request #208 from chrisuthe/dependabot/nuget/src/MultiRoomAudio/Microsoft.AspNetCore.OpenApi-8.0.25
- Merge pull request #226 from chrisuthe/fix/stale-repo-urls
- fix: correct stale squeezelite-docker repo URLs
- Bump YamlDotNet from 16.3.0 to 17.0.1
- Bump System.IO.Ports from 10.0.3 to 10.0.5
- Bump Microsoft.Extensions.Diagnostics.HealthChecks from 10.0.3 to 10.0.5
- Bump Microsoft.AspNetCore.OpenApi from 8.0.24 to 8.0.25

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
