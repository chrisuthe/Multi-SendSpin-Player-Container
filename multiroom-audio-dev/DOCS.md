# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-5b81d9c

**Current Dev Build Changes** (recent)

- Remove duplicate HidSharp package reference
- Merge pull request #191 from chrisuthe/claude/fix-relay-serial-port-fRVse
- Merge remote-tracking branch 'origin/main' into dev
- Merge pull request #189 from chrisuthe/scyto-patch-1
- Remove relay options from config.yaml
- Merge pull request #188 from chrisuthe/claude/fix-relay-serial-port-fRVse
- Remove unused relay_serial_port and relay_devices from HAOS add-on config
- Restore multiroom-audio/config.yaml to commit 5d448b53be765dec665f624e198e825ec0608a97
- Revert multiroom-audio/config.yaml to the state before commit af1f957711952b872169c6615d890dd2877ae4f3
- Revert commit af1f957711952b872169c6615d890dd2877ae4f3

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
