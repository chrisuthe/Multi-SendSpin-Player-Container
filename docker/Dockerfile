# Multi-Room Audio Controller - Unified Docker Image
# Works for both HAOS Add-on and standalone Docker deployments

# Build arguments for multi-arch support
ARG TARGETARCH

# Build arguments for version info (passed from CI)
ARG BUILD_VERSION=dev
ARG VCS_REF=unknown
ARG BUILD_DATE=unknown

# Base: Debian-slim for glibc compatibility
FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS base
WORKDIR /app
EXPOSE 8096

# Audio dependencies:
# - pulseaudio: Audio daemon (used for both HAOS and Docker modes)
# - libasound2: ALSA library (required for PulseAudio's ALSA output backend)
# - libasound2-plugins: ALSA plugins including PulseAudio bridge
# FTDI relay board dependencies:
# - libftdi1-2: libftdi1 library for FTDI USB device control
# - libusb-1.0-0: USB library required by libftdi
# USB HID relay board dependencies:
# - libudev1: Required by HidSharp for USB HID device enumeration
# CH340/Modbus serial relay board dependencies:
# - dialout group membership for serial port access (added below)
# Diagnostics dependencies:
# - usbutils: lsusb for USB device enumeration in diagnostics
RUN apt-get update && apt-get install -y --no-install-recommends \
    pulseaudio \
    libasound2 \
    libasound2-plugins \
    curl \
    libftdi1-2 \
    libusb-1.0-0 \
    libudev1 \
    usbutils \
    && rm -rf /var/lib/apt/lists/* \
    && usermod -a -G audio pulse 2>/dev/null || true \
    && usermod -a -G audio root 2>/dev/null || true \
    && usermod -a -G dialout root 2>/dev/null || true

# Build stage - using Debian SDK for consistent glibc builds
FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
ARG TARGETARCH
WORKDIR /src
COPY src/MultiRoomAudio/MultiRoomAudio.csproj MultiRoomAudio/
RUN dotnet restore "MultiRoomAudio/MultiRoomAudio.csproj"
COPY src/MultiRoomAudio/ MultiRoomAudio/
WORKDIR /src/MultiRoomAudio
# Note: Trimming disabled because SendSpin.SDK uses reflection
# Select RID based on target architecture for multi-arch builds
RUN case ${TARGETARCH} in \
        "amd64") RID="linux-x64" ;; \
        "arm64") RID="linux-arm64" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}" && exit 1 ;; \
    esac && \
    dotnet publish "MultiRoomAudio.csproj" \
        -c Release \
        -o /app/publish \
        -p:PublishSingleFile=true \
        --self-contained true \
        -r ${RID}

# Final image
FROM base AS final

# Re-declare build args (they don't persist across FROM)
ARG BUILD_VERSION
ARG VCS_REF
ARG BUILD_DATE

# Set version info as environment variables for the application
ENV APP_VERSION=${BUILD_VERSION}
ENV APP_BUILD_SHA=${VCS_REF}
ENV APP_BUILD_DATE=${BUILD_DATE}

WORKDIR /app
COPY --from=build /app/publish .

# Copy entrypoint script and PulseAudio config
COPY docker/entrypoint.sh /entrypoint.sh
COPY docker/pulse-system.pa /etc/pulse/system.pa
COPY docker/pulse-daemon.conf /etc/pulse/daemon.conf
RUN chmod +x /entrypoint.sh

# Create directories for both deployment modes
# /app/config, /app/logs - Docker mode
# /data, /share - HAOS mode
# /run/pulse - PulseAudio runtime directory
RUN mkdir -p /app/config /app/logs /data /share /run/pulse && \
    chmod 755 /run/pulse

# Disable D-Bus for PulseAudio (not available in container)
ENV DBUS_SESSION_BUS_ADDRESS=disabled
ENV PULSE_SYSTEM=1
ENV PULSE_SERVER=unix:/run/pulse/native

# Clear default ASPNETCORE_URLS to prevent "Overriding address" warning
# (.NET 8+ images default to http://+:8080, which conflicts with Kestrel config on port 8096)
ENV ASPNETCORE_URLS=

# Health check - verifies the API is responding
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8096/api/health || exit 1

# Entrypoint handles PulseAudio startup for Docker mode
ENTRYPOINT ["/entrypoint.sh"]
