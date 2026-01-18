# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-d3c7519

**Current Dev Build Changes** (recent)

- Add ApiExceptionHandler utility to reduce duplicated exception handling
- Merge branch 'dev' of https://github.com/chrisuthe/Multi-SendSpin-Player-Container into dev
- add back aarch64 to the config so it can get the already building images.
- Merge branch 'main' into dev
- Merge pull request #69 from scyto/feature/preserve-volume-across-tracks
- Fix hardware volume init to always apply volume, not skip
- Fix UI slider interaction and tooltip issues
- Add volume grace period to resolve startup volume sync battle
- Fix tooltip persistence issues by properly disposing old instances

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
