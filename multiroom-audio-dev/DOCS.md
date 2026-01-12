# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-b4c0f7e

**Current Dev Build Changes** (recent)

- Sprint 3: Per-player hardware volume and sink card UI improvements
- Sprint 2: Player rename hint, card profile unmute, consistent device names
- Fix Sprint 1 issues: wizard filtering, modal stacking, validation
- Proper Sink Error handling + sink edit screen
- Fix Auto-Naming Problems
- load things in the right order.
- Sinks should error out when they can't be created. Don't try to use MDNS more than once at a time.
- Fix linter formatting errors
- Fix Player not started during onboarding
- Add SOund Generation/Test Tone to sink creation page

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
