# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-c092dab

**Current Dev Build Changes** (recent)

- release: prepare v5.1.0
- Merge branch 'main' into dev
- Merge pull request #204 from chrisuthe/dependabot/github_actions/docker/build-push-action-7
- Merge pull request #205 from chrisuthe/dependabot/github_actions/docker/login-action-4
- Merge pull request #203 from chrisuthe/dependabot/github_actions/docker/setup-buildx-action-4
- Merge pull request #186 from chrisuthe/dependabot/nuget/src/MultiRoomAudio/System.IO.Ports-10.0.3
- Merge pull request #202 from chrisuthe/dependabot/github_actions/docker/metadata-action-6
- Bump docker/setup-buildx-action from 3 to 4
- Merge pull request #201 from chrisuthe/dependabot/github_actions/docker/setup-qemu-action-4
- Bump docker/login-action from 3 to 4

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

## Configuration

### Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `log_level` | string | `info` | Logging verbosity (debug, info, warning, error) |
| `mock_hardware` | bool | `false` | Enable mock audio devices and relay boards for testing without hardware |
| `enable_advanced_formats` | bool | `false` | Show format selection UI (players default to flac-48000 regardless) |

## For Stable Release

Use the "Multi-Room Audio Controller" add-on (without "Dev") for stable releases.
