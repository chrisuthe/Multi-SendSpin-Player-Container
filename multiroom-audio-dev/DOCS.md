# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-5909b26

**Current Dev Build Changes** (recent)

- Merge pull request #167 from scyto/bug/bluez-card-sink-matching
- Add comprehensive BlueZ support across all card/sink matching
- Fix BlueZ cards not finding their sinks for max volume/mute
- Merge pull request #123 from scyto/feature/fix-startup-crackle
- Add player name prefix to SDK log messages
- Skip invalid 0ms latency samples during lock-in
- Merge pull request #122 from scyto/feature/fix-startup-crackle
- Revert startup deadband to 50ms
- Merge pull request #121 from scyto/feature/fix-startup-crackle
- Reduce startup deadband from 50ms to 30ms for multi-room sync

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
