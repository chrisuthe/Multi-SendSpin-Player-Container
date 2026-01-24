# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-d6319cf

**Current Dev Build Changes** (recent)

- Merge pull request #115 from scyto/dev
- Fix local volume changes not updating MA UI
- Fix volume control: player is now authoritative for its own volume
- Merge pull request #114 from scyto/dev
- Fix volume sync: use SendPlayerStateAsync to update MA state
- Fix Kestrel address override warning in Docker
- Merge pull request #112 from scyto/dev
- Fix mute state not echoing back to MA server
- Promote mute and volume sync logs from debug to info level
- Fix LOG_LEVEL env var not working due to appsettings.json override

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
