# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-f07c6b3

**Current Dev Build Changes** (recent)

- Merge pull request #175 from scyto/dev
- Fix off-profile detection for Intel HDA combined duplex profiles (#139)
- Fix race condition in device reconnection causing duplicate restarts (#138)
- Fix off-profile device detection to ignore IsAvailable flag (#137)
- Show off-profile cards in device selector (#136)
- Fix player state for empty device and diagnostics device matching (#135)
- Merge pull request #173 from scyto/dev
- Fix diagnostics summary including custom sinks in audio devices (#134)
- Fix vertical alignment of mute button with slider (#133)
- Fix duplicate toast messages on server reconnection (#132)

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
