# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-2b1f17d

**Current Dev Build Changes** (recent)

- Refactor controllers: move batch logic to service, standardize patterns
- Extract StartupDiagnosticsService and add UpdateDeviceProperty helper
- Update all user-facing references from "Initial Volume" to "Startup Volume"
- Merge branch 'dev' of https://github.com/chrisuthe/Multi-SendSpin-Player-Container into feature/rename-initial-volume-to-startup-volume
- Use FireAndForget helper for async player connection and broadcast
- Add thread safety to DefaultPaParser with file locking
- Add YamlFileService and PactlCommandRunner utilities to reduce code duplication
- Add ApiExceptionHandler utility to reduce duplicated exception handling
- Rename "Initial Volume" to "Startup Volume" in player edit dialog
- Merge branch 'dev' of https://github.com/chrisuthe/Multi-SendSpin-Player-Container into dev

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
