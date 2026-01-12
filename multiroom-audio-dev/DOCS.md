# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-bc93967

**Current Dev Build Changes** (recent)

- load things in the right order.
- Sinks should error out when they can't be created. Don't try to use MDNS more than once at a time.
- Fix linter formatting errors
- Fix Player not started during onboarding
- Add SOund Generation/Test Tone to sink creation page
- Fix Onboarding Wizard not launching on Fresh Install
- Fix players where the hardware has changed/is not available to not lock and be uneditable.
- Fix Linter Errors
- Merge pull request #41 from chrisuthe/onboarding
- Merge origin/dev into onboarding

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
