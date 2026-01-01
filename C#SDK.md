# Plan: Replace Sendspin CLI with C# SDK

## Summary
Create a C# microservice using SendSpin.SDK v2.0.0 that the Python Flask backend calls via REST API. Deploy as a new `Dockerfile.sdk` image variant.

## Architecture

```
[Python Flask Backend]
       │
       │ HTTP REST (localhost:5100)
       ▼
[C# Sendspin Service] ──► [SendSpin.SDK v2.0.0]
       │
       ▼
   [Audio Hardware]
```

---

## Phase 1: C# Microservice

### 1.1 Create Project Structure
```
sendspin-service/
├── SendspinService.csproj
├── Program.cs
├── Endpoints/
│   ├── PlayersEndpoint.cs
│   └── DevicesEndpoint.cs
├── Services/
│   └── PlayerManagerService.cs
└── Models/
    ├── PlayerConfig.cs
    └── PlayerStatus.cs
```

### 1.2 NuGet Dependencies
- `SendSpin.SDK` (2.0.0)
- `Microsoft.AspNetCore.OpenApi`
- `Swashbuckle.AspNetCore` (for Swagger docs)

### 1.3 REST API Endpoints
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/players` | List all active players |
| POST | `/api/players` | Create and start a player |
| DELETE | `/api/players/{name}` | Stop and remove player |
| PUT | `/api/players/{name}/device` | Hot-switch audio device |
| GET | `/api/devices` | List PortAudio devices |
| GET | `/api/health` | Service health check |

### 1.4 Key Implementation Details
- Use `ConcurrentDictionary<string, IAudioPlayer>` for player tracking
- Implement `IHostedService` for graceful shutdown
- Expose SDK features: mDNS discovery, codec selection, clock sync
- Use `SwitchDeviceAsync()` for device changes without restart

---

## Phase 2: Python Integration

### 2.1 New Provider: `app/providers/sendspin_sdk.py`
```python
class SendspinSdkProvider(PlayerProvider):
    provider_type = "sendspin"
    display_name = "Sendspin (SDK)"
    binary_name = None  # Not subprocess-based

    def __init__(self, service_url="http://localhost:5100"):
        self.service_url = service_url

    # Override build_command to raise NotImplementedError
    # Implement start_player/stop_player via REST calls
```

### 2.2 Modify `app/app.py`
- Add `SendspinSdkProvider` registration
- Check `SENDSPIN_SDK_URL` environment variable
- Provider talks to C# service instead of ProcessManager

### 2.3 Modify `app/common.py`
- Update `/api/devices/portaudio` to call C# service
- Remove `sendspin --list-audio-devices` subprocess call

### 2.4 New Dependency
Add to `requirements.txt`:
```
requests>=2.31.0  # Already present, verify version
```

---

## Phase 3: Docker SDK Image

### 3.1 Create `Dockerfile.sdk`
```dockerfile
# Stage 1: Build C# service
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build
WORKDIR /src
COPY sendspin-service/ .
RUN dotnet publish -c Release -o /app/publish \
    --self-contained true \
    -p:PublishSingleFile=true

# Stage 2: Python + .NET runtime
FROM python:3.12-slim-bookworm

# Install audio dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    libportaudio2 alsa-utils supervisor curl \
    && rm -rf /var/lib/apt/lists/*

# Copy C# service (self-contained, no .NET runtime needed)
COPY --from=dotnet-build /app/publish/sendspin-service /usr/local/bin/

# Python setup
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app/ /app/
COPY supervisord-sdk.conf /etc/supervisor/conf.d/supervisord.conf
COPY entrypoint-sdk.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENV SENDSPIN_SDK_URL=http://localhost:5100
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=10s \
    CMD curl -f http://localhost:8080/api/players && \
        curl -f http://localhost:5100/api/health || exit 1

ENTRYPOINT ["/app/entrypoint.sh"]
```

### 3.2 Create `supervisord-sdk.conf`
```ini
[supervisord]
nodaemon=true

[program:sendspin-service]
command=/usr/local/bin/sendspin-service
autostart=true
autorestart=true
priority=10
stdout_logfile=/app/logs/sendspin-service.log

[program:flask-app]
command=python3 app_enhanced.py
directory=/app
autostart=true
autorestart=true
priority=20
stdout_logfile=/app/logs/flask.log
```

### 3.3 Update GitHub Actions
Add new job in `docker-publish.yml`:
```yaml
build-sdk:
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    - uses: docker/build-push-action@v5
      with:
        file: Dockerfile.sdk
        platforms: linux/amd64,linux/arm64
        tags: ghcr.io/chrisuthe/multiroom-audio-sdk:latest
```

---

## Phase 4: Configuration

### 4.1 Player Config Compatibility
Existing configs work unchanged:
```yaml
Kitchen:
  name: Kitchen
  device: "0"
  provider: sendspin
  delay_ms: 50
```

### 4.2 New SDK-Specific Options (optional)
```yaml
Kitchen:
  name: Kitchen
  device: "0"
  provider: sendspin
  # New SDK options:
  codec: opus          # opus, flac, pcm
  buffer_size_ms: 100  # Playback buffer
```

---

## Files to Create

| File | Description |
|------|-------------|
| `sendspin-service/SendspinService.csproj` | C# project file |
| `sendspin-service/Program.cs` | ASP.NET Minimal API entry |
| `sendspin-service/Endpoints/PlayersEndpoint.cs` | Player CRUD endpoints |
| `sendspin-service/Endpoints/DevicesEndpoint.cs` | Device enumeration |
| `sendspin-service/Services/PlayerManagerService.cs` | SDK player lifecycle |
| `sendspin-service/Models/PlayerConfig.cs` | Request/response DTOs |
| `app/providers/sendspin_sdk.py` | New Python provider |
| `Dockerfile.sdk` | SDK image build |
| `supervisord-sdk.conf` | Process supervision |
| `entrypoint-sdk.sh` | Container entrypoint |

## Files to Modify

| File | Changes |
|------|---------|
| `app/app.py` | Register `SendspinSdkProvider` |
| `app/common.py` | Update device enumeration route |
| `.github/workflows/docker-publish.yml` | Add SDK image build job |
| `requirements.txt` | Verify `requests` version |

---

## Testing Strategy

1. **Unit Tests**: Mock HTTP calls in `SendspinSdkProvider`
2. **Integration Tests**: Docker Compose with both services
3. **Manual Tests**:
   - Player start/stop via web UI
   - Device hot-switching
   - mDNS server discovery

---

## Rollout

1. SDK image is opt-in (`Dockerfile.sdk`)
2. Existing images (`Dockerfile`, `Dockerfile.slim`, HAOS) unchanged
3. Users migrate by switching to SDK image when ready
4. Future: Consider making SDK the default in a major version
