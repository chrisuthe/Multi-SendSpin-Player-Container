# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-d278cad

**Current Dev Build Changes** (recent)

- Merge pull request #125 from scyto/feature/handle-all-pipeline-states
- Merge pull request #124 from chrisuthe/feature/graceful-start-stop-and-disconnects
- Update SendSpin.SDK package version to 6.0.1
- Merge pull request #123 from scyto/dev
- Downgrade expected PulseAudio disconnect logs from warning to debug
- Handle all SDK pipeline states instead of logging warnings
- Suppress noisy SDK mDNS errors and improve connection failure logging
- Log mDNS discovery failures as warnings instead of errors
- Serve web UI before any service initialization and speed up reconnection
- Defer startup orchestration until Kestrel is listening

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
