# Contributing to Multi-Room Audio Controller

Thank you for your interest in contributing to this project!

## Quick Start for Contributors

1. **Fork the repository** on GitHub
2. **Clone your fork**: `git clone https://github.com/yourusername/Multi-SendSpin-Player-Container.git`
3. **Create a feature branch**: `git checkout -b feature/amazing-feature`
4. **Make your changes** and test thoroughly
5. **Build and verify**: `dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj`
6. **Commit your changes**: `git commit -m 'Add amazing feature'`
7. **Push to your branch**: `git push origin feature/amazing-feature`
8. **Open a Pull Request**

## Development Setup

### Prerequisites

- .NET 8.0 SDK
- Docker (for testing containerized builds)
- Linux environment recommended for audio testing (or WSL2 on Windows)

### Local Development

```bash
# Restore dependencies
dotnet restore src/MultiRoomAudio/MultiRoomAudio.csproj

# Build the project
dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj

# Run locally (audio features require Linux)
dotnet run --project src/MultiRoomAudio/MultiRoomAudio.csproj

# Access the web interface
# Open http://localhost:8096
```

### Docker Development

```bash
# Build the Docker image
docker build -f docker/Dockerfile -t multiroom-audio:dev .

# Run with audio devices (Linux only)
docker run -d --name multiroom-dev \
  -p 8096:8096 \
  --device /dev/snd \
  -v $(pwd)/config:/app/config \
  multiroom-audio:dev

# View logs
docker logs -f multiroom-dev
```

### HAOS Add-on Development

```bash
# Build the add-on image locally
docker build -f docker/Dockerfile \
  --platform linux/amd64 \
  -t multiroom-audio-addon:local .

# Test locally (without full HAOS integration)
docker run --rm -it -p 8096:8096 multiroom-audio-addon:local
```

## Project Structure

```
squeezelite-docker/
├── src/
│   └── MultiRoomAudio/          # Main C# application
│       ├── Audio/               # PortAudio integration
│       ├── Controllers/         # REST API endpoints
│       ├── Models/              # Data models
│       ├── Services/            # Business logic
│       ├── Utilities/           # Helpers
│       ├── wwwroot/             # Static web UI
│       └── Program.cs           # Entry point
├── docker/
│   └── Dockerfile               # Unified Alpine image
├── multiroom-audio/             # HAOS add-on metadata
│   ├── config.yaml
│   ├── CHANGELOG.md
│   └── DOCS.md
└── docs/                        # Documentation
```

## Coding Guidelines

### C#

- **Target Framework**: .NET 8.0
- **Nullable**: Enabled project-wide (use nullable reference types)
- **Style**: Follow Microsoft C# coding conventions
- **Documentation**: XML doc comments for public APIs

```csharp
/// <summary>
/// Creates and starts a new audio player.
/// </summary>
/// <param name="request">Player configuration request.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The created player response.</returns>
public async Task<PlayerResponse> CreatePlayerAsync(
    PlayerCreateRequest request,
    CancellationToken ct = default)
{
    // Implementation
}
```

### JavaScript (Web UI)

- Vanilla JavaScript only - no external frameworks
- ES6+ features (const/let, arrow functions, template literals)
- Use `textContent` instead of `innerHTML` for XSS prevention

### Docker

- Use multi-stage builds for smaller images
- Target Alpine Linux for production
- Include health checks

## What We're Looking For

- **Bug fixes** - Help make it more stable
- **Performance improvements** - Optimize audio handling
- **UI/UX enhancements** - Better web interface design
- **Documentation** - Better setup guides, troubleshooting
- **HAOS improvements** - Better Home Assistant integration
- **Platform support** - Testing on different Linux distros

## Testing

### Manual Testing Checklist

Before submitting a PR, verify:

```bash
# Build succeeds
dotnet build src/MultiRoomAudio/MultiRoomAudio.csproj

# Application starts
dotnet run --project src/MultiRoomAudio/MultiRoomAudio.csproj

# API endpoints work
curl http://localhost:8096/api/players
curl http://localhost:8096/api/devices
curl http://localhost:8096/api/health
```

### Docker Testing

```bash
# Image builds successfully
docker build -f docker/Dockerfile -t test .

# Container starts and responds
docker run -d -p 8096:8096 test
curl http://localhost:8096/api/health
```

## Code of Conduct

Be respectful, helpful, and inclusive. This is a community project for everyone to enjoy better multi-room audio!

## For Maintainers: Release Process

When releasing a new version of the HAOS add-on:

1. **Do NOT manually edit** `multiroom-audio/config.yaml` version
2. Update `multiroom-audio/CHANGELOG.md` with release notes
3. Create and push a tag:
   ```bash
   git tag -a v2.0.0 -m "v2.0.0 - C# rewrite"
   git push --tags
   ```
4. CI will automatically:
   - Build the Docker image
   - Update `config.yaml` version after successful build
   - HAOS users see the update only when the image is ready

## Questions?

Open an issue for discussion before major changes. We're happy to help guide contributions!

## AI Disclosure

This project is developed with the assistance of AI coding tools. Contributions from both human and AI-assisted development are welcome, provided they meet quality standards.
