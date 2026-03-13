# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-bf1eb98

**Current Dev Build Changes** (recent)

- Merge pull request #200 from chrisuthe/chore/SDK-Update-730
- chore: upgrade SendSpin.SDK 7.2.1 -> 7.3.0
- Merge pull request #199 from scyto/dev
- feat: add mono output mode to wizard remap sink UI (#140)
- Merge pull request #198 from scyto/bug/fix-ingress-buffer-fetch
- fix: use relative paths for buffer settings fetch calls
- Merge pull request #197 from chrisuthe/task/feat-adjustable-buffer
- fix: convert PulseAudio config files to LF line endings
- docs: add BUFFER_SECONDS to environment variables table
- feat: add System Settings modal with buffer size slider

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
