# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-f8f69af

**Current Dev Build Changes** (recent)

- Persist volume changes to survive container restarts
- Add anti-oscillation debounce to sync correction
- Update stats display to match 15ms correction threshold
- Add latency lock-in to reduce sync corrections from PulseAudio jitter
- Expose HAOS add-on options as environment variables
- Update SDK to 6.1.1 and fix scheduled start timing issue
- Merge pull request #125 from scyto/feature/handle-all-pipeline-states
- Merge pull request #124 from chrisuthe/feature/graceful-start-stop-and-disconnects
- Update SendSpin.SDK package version to 6.0.1
- Merge pull request #123 from scyto/dev

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
