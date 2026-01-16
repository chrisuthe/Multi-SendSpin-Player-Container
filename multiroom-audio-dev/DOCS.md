# Multi-Room Audio (Dev)

<!-- VERSION_INFO_START -->
## Development Build: sha-d64f251

**Current Dev Build Changes** (recent)

- Merge pull request #66 from scyto/dev
- Merge pull request #68 from scyto/feature/fix-goodbye-reason-protocol
- Merge pull request #67 from scyto/feature/autostart-reconnection-fix
- Fix: Use valid SendSpin protocol GoodbyeReason values
- Fix: Remove failed player before reconnection attempt
- Fix: Queue failed autostart players for automatic reconnection
- more readme.md OCD
- one real change, rest is my vscode OCD
- Fix health endpoint, was on ./health not ./api/health as documented
- Remove reference to Multi-SendSpin-Player-Container/tests/MultiRoomAudio.Tests/MultiRoomAudio.Tests.csproj as my vscode was objecting on a dotnet build

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
