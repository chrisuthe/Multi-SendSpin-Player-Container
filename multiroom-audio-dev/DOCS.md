# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-d41de60

**Current Dev Build Changes** (recent)

- Refactor exception handling to use typed exceptions
- Merge pull request #82 from scyto/feature/12v-trigger-plus-mock-hardware
- Add logging when pactl process fails to start in diagnostics
- Fix null coalescing operator precedence bug in SetDeviceMaxVolume
- Update SinksEndpoint to use --channel-map, remove dead code
- Add --no-remix flag to prevent PulseAudio channel upmixing
- Use paplay --channel-map for multi-channel test tones
- Fix test tone routing for multi-channel devices
- Fix test tone routing for remap sinks, reduce tone volume
- Add configurable mock hardware via YAML

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
