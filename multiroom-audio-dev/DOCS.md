# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-00a8a40

**Current Dev Build Changes** (recent)

- Update player details UI after output format removal
- Remove BitDepthConverter and output format configuration
- cleanup ALSA destruction
- Remove resampling - use direct passthrough to PulseAudio
- Load ALSA sinks at higher sample rates when supported
- Use module-alsa-sink for direct PCM device access
- Add debug output and /proc/asound mount instructions
- Fix ALSA device detection in Docker standalone mode
- Fix Docker standalone mode to use PulseAudio
- Remove ALSA support, standardize on PulseAudio

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
