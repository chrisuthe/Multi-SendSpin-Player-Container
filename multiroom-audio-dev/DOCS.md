# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-e246637

**Current Dev Build Changes** (recent)

- Merge pull request #112 from scyto/dev
- Fix mute state not echoing back to MA server
- Promote mute and volume sync logs from debug to info level
- Fix LOG_LEVEL env var not working due to appsettings.json override
- Fix mute sync: echo state back to MA and add GroupState debug logging
- Fix mute state not syncing: update Player.IsMuted after pipeline mute
- Add mute button to player card with bidirectional MA sync
- Merge pull request #111 from scyto/dev
- Refactor stats panel to update values incrementally instead of full DOM rebuild
- Cache hardware info in frontend on first stats fetch

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
