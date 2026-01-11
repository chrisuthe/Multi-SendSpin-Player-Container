# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-1757dce

**Current Dev Build Changes** (recent)

- Restructure Menus
- Add Logging and Testing
- stop touching the volume, MA does that. Set hardware to 80% and passthrough what we get from MA to the player (we dont' control any volume)
- Volume logging and get rid of adjustments before applying
- add card profile UI
- fix double volume control
- Bump SDK version to allow initial volume setting, set volume.
- attempt to fix volume issue
- Add startup logging for discovered sound cards
- support loading by card

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
