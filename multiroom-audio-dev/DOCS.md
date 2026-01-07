# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-3638793

**Current Dev Build Changes** (recent)

- Remove ALSA support, standardize on PulseAudio
- Update SendSpin.SDK to v3.5.1
- add ALSA calibration passing to the SDK, updated to 3.5.0 and removed deadband entry/exit.
- Fix Native Rate Option
- add alsa calibration
- test another theroy
- test theory on clock timing
- Testing Theory on Sync Error
- Add capability to use legacy timing to do A/B Testing
- Attempt to diagnose timing issues by adding native rate option

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
