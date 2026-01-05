#!/bin/bash
set -e

# Multi-Room Audio Container Entrypoint
# - HAOS mode: Uses system PulseAudio (provided by supervisor)
# - Docker standalone: Uses direct ALSA (supports asound.conf zones)

echo "========================================="
echo "Multi-Room Audio Controller Starting"
echo "========================================="

# Check if we're running in HAOS add-on mode
# HAOS provides PulseAudio via the audio: true config option
if [ -f "/data/options.json" ] || [ -n "$SUPERVISOR_TOKEN" ]; then
    echo "Detected HAOS add-on mode - using system PulseAudio"

    # HAOS supervisor mounts:
    # - /etc/pulse/client.conf (contains default-server pointing to /run/audio)
    # - /run/audio (the PulseAudio socket directory)
    # - /etc/asound.conf (ALSA config)

    # Ensure PULSE_SERVER is set correctly for HAOS
    # HAOS uses /run/audio/pulse.sock as the socket path
    if [ -S "/run/audio/pulse.sock" ]; then
        export PULSE_SERVER="unix:/run/audio/pulse.sock"
        echo "Found PulseAudio socket at /run/audio/pulse.sock"
    elif [ -S "/run/audio/native" ]; then
        export PULSE_SERVER="unix:/run/audio/native"
        echo "Found PulseAudio socket at /run/audio/native"
    elif [ -d "/run/audio" ]; then
        # Fallback - let pactl try to find it
        export PULSE_SERVER="unix:/run/audio/pulse.sock"
        echo "PulseAudio directory exists at /run/audio (socket may not be ready)"
    fi

    # Show mounted audio config for diagnostics
    echo ""
    if [ -f "/etc/pulse/client.conf" ]; then
        echo "PulseAudio client.conf (mounted by supervisor):"
        cat /etc/pulse/client.conf 2>/dev/null | head -20
        echo ""
    else
        echo "WARNING: /etc/pulse/client.conf not found - audio may not work"
    fi

    # Wait for PulseAudio socket to be available
    echo "Waiting for PulseAudio to become available..."
    MAX_WAIT=30
    WAIT_COUNT=0

    # First wait for the socket file to exist (HAOS uses pulse.sock)
    while [ ! -S "/run/audio/pulse.sock" ] && [ ! -S "/run/audio/native" ] && [ $WAIT_COUNT -lt $MAX_WAIT ]; do
        WAIT_COUNT=$((WAIT_COUNT + 1))
        if [ $((WAIT_COUNT % 5)) -eq 0 ]; then
            echo "Waiting for PulseAudio socket... (${WAIT_COUNT}s)"
            ls -la /run/audio/ 2>/dev/null || echo "  /run/audio not mounted yet"
        fi
        sleep 1
    done

    # Update PULSE_SERVER if socket appeared
    if [ -S "/run/audio/pulse.sock" ]; then
        export PULSE_SERVER="unix:/run/audio/pulse.sock"
    elif [ -S "/run/audio/native" ]; then
        export PULSE_SERVER="unix:/run/audio/native"
    fi

    # Then verify pactl can connect
    WAIT_COUNT=0
    while ! pactl info >/dev/null 2>&1; do
        WAIT_COUNT=$((WAIT_COUNT + 1))
        if [ $WAIT_COUNT -ge $MAX_WAIT ]; then
            echo ""
            echo "WARNING: PulseAudio not responding after ${MAX_WAIT}s"
            echo "Diagnostics:"
            echo "  PULSE_SERVER=$PULSE_SERVER"
            echo "  pulse.sock exists: $([ -S /run/audio/pulse.sock ] && echo yes || echo no)"
            echo "  native exists: $([ -S /run/audio/native ] && echo yes || echo no)"
            ls -la /run/audio/ 2>/dev/null || echo "  /run/audio directory not found"
            echo ""
            echo "Starting anyway - audio devices may not be available."
            echo "Check HAOS audio configuration: Settings > System > Audio"
            break
        fi
        if [ $((WAIT_COUNT % 5)) -eq 0 ]; then
            echo "Waiting for PulseAudio server... (${WAIT_COUNT}s)"
        fi
        sleep 1
    done

    if pactl info >/dev/null 2>&1; then
        echo "PulseAudio is ready!"
        echo ""
        echo "PulseAudio server info:"
        pactl info 2>/dev/null | grep -E "^(Server|Default Sink|Default Source)" || true
        echo ""
        echo "Available PulseAudio sinks:"
        pactl list sinks short 2>/dev/null || echo "  (none detected)"
    fi
    echo ""

    exec ./MultiRoomAudio "$@"
fi

# Standalone Docker mode - use direct ALSA (no PulseAudio needed)
# This supports custom zones defined in /etc/asound.conf
echo "Standalone Docker mode - using direct ALSA backend"
echo ""

# List available ALSA devices for diagnostics
echo "ALSA hardware devices (aplay -l):"
if command -v aplay >/dev/null 2>&1; then
    aplay -l 2>/dev/null || echo "  (none found - check /dev/snd mount)"
else
    echo "  (aplay not available)"
fi
echo ""

# Show all devices including software-defined zones (aplay -L)
echo "All ALSA devices including software zones (aplay -L):"
aplay -L 2>/dev/null | head -50 || echo "  (none)"
echo ""

# Check for custom asound.conf
if [ -f /etc/asound.conf ]; then
    echo "Custom /etc/asound.conf detected - software zones will be available"
    echo ""
fi

echo "========================================="
echo "Starting Multi-Room Audio application"
echo "========================================="

# Run the application directly - it will use ALSA backend
exec ./MultiRoomAudio "$@"
