# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-fa19c3d

**Current Dev Build Changes** (recent)

- Fix Pulse Starting when It shouldn't
- feat: show dynamic version/build info on startup
- fix: push dev HASSIO image with SHA tag for HAOS compatibility
- Filter more ALSA plugin devices
- Update ALSA for graceful reconnect
- - Add startup check if on HAOS to make sure pulseaudio is ready - Add reconnection if connection drops to audio provider - Add friendly names where possible to device dropdown
- use direct ALSA where available, show more devices that are software configured.
- fix dev vs stable builds
- Merge branch 'dev' of https://github.com/chrisuthe/Multi-SendSpin-Player-Container into dev
- clean up stable vs dev

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
