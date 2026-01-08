# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-12d70ba

**Current Dev Build Changes** (recent)

- pulseplayer updates and stats for nerds fix
- read vs readraw
- Add sink creation, attempt bugfix of playback.
- shift to SDK 5.0.0- correction in app vs in SDK, drop/insert only.
- Shifted from PA_SimpleAudio to PA_stream.
- try to have the right amount of latency.
- Add 20ms additional latency I guess. Plus console cleanup.
- clear out the console on start
- Update player details UI after output format removal
- Remove BitDepthConverter and output format configuration

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
