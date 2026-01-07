# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-720a756

**Current Dev Build Changes** (recent)

- Attempt to diagnose timing issues by adding native rate option
- Fix ALSA latency detection and delay offset functionality
- Add Stats for Nerds feature to CHANGELOG
- Update documentation for unified resampler and Stats for Nerds
- Add Stats for Nerds real-time diagnostics panel
- Update SendSpin.SDK to version 3.3.1
- Add unified polyphase resampler for high-quality sample rate conversion
- Fix TPDF dithering to use triangular probability distribution
- Show device capabilities and what is being used
- Fix 32-bit output

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
