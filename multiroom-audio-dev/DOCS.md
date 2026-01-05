# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-42852f4

**Current Dev Build Changes** (recent)

- Update ALSA for graceful reconnect
- - Add startup check if on HAOS to make sure pulseaudio is ready - Add reconnection if connection drops to audio provider - Add friendly names where possible to device dropdown
- use direct ALSA where available, show more devices that are software configured.
- fix dev vs stable builds
- Merge branch 'dev' of https://github.com/chrisuthe/Multi-SendSpin-Player-Container into dev
- clean up stable vs dev
- docs: add 2.0.12 changelog entry for audio quality fixes
- fix: configure PulseAudio sync options to disable frame drop/insert

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
