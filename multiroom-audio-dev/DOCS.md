# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-39f463d

**Current Dev Build Changes** (recent)

- Merge pull request #151 from scyto/bug/sync-threshold
- Revert sync correction threshold from 30ms back to 15ms
- Merge pull request #150 from scyto/feature/remove-default-device-option
- Remove 'Default Device' option from player device dropdown
- Merge pull request #149 from scyto/dev
- Use bus path as primary key for device/card config persistence
- Merge pull request #148 from scyto/bug/usb-unplug-wrong-device
- Fix audio playing on wrong device when USB is unplugged
- Merge pull request #147 from scyto/bug/usb-unplug-deadlock-upstream
- Fix deadlock when USB audio device is unplugged

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
