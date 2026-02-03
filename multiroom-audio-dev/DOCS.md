# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-5e07e5e

**Current Dev Build Changes** (recent)

- Add server-side log download endpoint to export all logs
- Bump Sendspin.SDK to 6.3.1 for sync correction logging
- Fix audio clock to return Unix epoch microseconds
- Fix audio clock crash when called from PulseAudio callback thread
- segfault crash fix woops
- Add audio hardware clock support for VM-resilient sync timing
- Merge pull request #140 from scyto/bug/docs-revisions
- Remove async from importSink (nothing is async) add logging via SDK 6.2.0-preview2
- Bump SDK to Attempt Monotonic Timer Fix
- docs: Add Docker dev builds guide and improve compose files

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
