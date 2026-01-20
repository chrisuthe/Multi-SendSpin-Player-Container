# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-5945fe9

**Current Dev Build Changes** (recent)

- Merge pull request #86 from scyto/dev
- Add 12V trigger documentation and README feature mention
- Fix UI relay test for Modbus boards with slashes in ID
- Add Modbus ASCII relay board support for CH340/CH341 devices
- Merge pull request #85 from scyto/feature/sink-description-special-chars
- ahh there was a bug in pulseaudio!
- and another try
- add debugging log as & in sink description field  still causing issues
- Fix SanitizeDescription for Process.ArgumentList usage
- Fix sink description handling for special characters

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
