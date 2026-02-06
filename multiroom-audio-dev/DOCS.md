# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-e74a7bc

**Current Dev Build Changes** (recent)

- i fucking hate USB devices
- Merge pull request #96 from scyto/feature/auto-reconnect-option
- Fix auto-resume not saved when creating new player
- Add auto-resume option for device reconnection
- Merge pull request #95 from scyto/bug/grace-period-cts-disposal
- Fix CTS disposal timing in grace period debouncing
- Merge pull request #94 from scyto/bug/hid-enable-on-running-player
- Merge pull request #93 from scyto/feature/device-loss-grace-period
- Start HID reader immediately when enabling on running player
- Check subscription service IsReady before device reconnection

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

## For Stable Release

Use the "Multi-Room Audio Controller" add-on (without "Dev") for stable releases.
