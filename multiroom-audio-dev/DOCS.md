# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-5db9f06

**Current Dev Build Changes** (recent)

- Fix whitespace formatting in PlayerStatsMapper
- Merge branch 'dev' of https://github.com/chrisuthe/Multi-SendSpin-Player-Container into dev
- Fix stats for nerds blocking at high bitrates (192kHz 24-bit)
- Merge pull request #107 from scyto/ftdi-clean
- Fix FTDI OpenByPathHash crash: use single context for enumeration and open
- Use USB path-based IDs for all FTDI boards (multi-board support)
- Add Denkovi 4/8 channel relay board support with model-specific UI
- Add FTDI relay hardware state verification and logging
- Use synchronous bit-bang mode for FTDI relay boards
- Merge branch 'main' into dev

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
