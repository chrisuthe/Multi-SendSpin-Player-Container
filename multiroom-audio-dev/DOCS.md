# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-5420907

**Current Dev Build Changes** (recent)

- Fix null coalescing operator precedence bug in SetDeviceMaxVolume
- chore: remove outdated nextgen.md implementation plan
- Add AppArmor profile to dev add-on for improved security
- Extract BackgroundTaskExecutor utility from PlayerManagerService
- Refactor long methods in PlayerManagerService for improved readability
- Add XML documentation to endpoint extensions and validation attributes
- Improve thread safety with ReaderWriterLockSlim and disposal patterns
- Add AppArmor profile and improve HAOS security rating
- Add structured error handling and audio system documentation
- Refactor controllers: move batch logic to service, standardize patterns

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
