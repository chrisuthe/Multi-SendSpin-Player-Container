# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-a4df6fb

**Current Dev Build Changes** (recent)

- Fix code formatting (dotnet format)
- Fix: Read mock_hardware from HAOS options.json for DI registration
- Add HAOS config options and mock hardware toggle
- Add 12V trigger documentation and README feature mention
- Fix UI relay test for Modbus boards with slashes in ID
- Add Modbus ASCII relay board support for CH340/CH341 devices
- Refactor exception handling to use typed exceptions
- Merge pull request #82 from scyto/feature/12v-trigger-plus-mock-hardware
- Add logging when pactl process fails to start in diagnostics
- Fix null coalescing operator precedence bug in SetDeviceMaxVolume

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
