#!/bin/bash
set -e

# Multi-Room Audio Container Entrypoint
# - HAOS mode: Uses system PulseAudio (provided by supervisor)
# - Docker standalone: Starts local PulseAudio daemon

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

# Standalone Docker mode - start local PulseAudio daemon
echo "Standalone Docker mode - starting local PulseAudio daemon"
echo ""

# Ensure runtime directory exists with correct permissions
mkdir -p /run/pulse
chmod 755 /run/pulse

# Start PulseAudio in system mode (daemon)
# Using --disallow-exit to prevent auto-shutdown, --exit-idle-time=-1 to never exit
echo "Starting PulseAudio daemon..."
pulseaudio --system --disallow-exit --exit-idle-time=-1 --daemonize=yes \
    --log-target=stderr --log-level=notice 2>&1 || {
    echo "ERROR: Failed to start PulseAudio daemon"
    echo "Trying with verbose logging..."
    pulseaudio --system --disallow-exit --exit-idle-time=-1 --daemonize=no \
        --log-target=stderr --log-level=debug &
    sleep 2
}

# Set PULSE_SERVER for the application
export PULSE_SERVER="unix:/run/pulse/native"

# Wait for PulseAudio to be ready
echo "Waiting for PulseAudio to become available..."
MAX_WAIT=30
WAIT_COUNT=0

while ! pactl info >/dev/null 2>&1; do
    WAIT_COUNT=$((WAIT_COUNT + 1))
    if [ $WAIT_COUNT -ge $MAX_WAIT ]; then
        echo ""
        echo "ERROR: PulseAudio not responding after ${MAX_WAIT}s"
        echo "Diagnostics:"
        echo "  PULSE_SERVER=$PULSE_SERVER"
        echo "  Socket exists: $([ -S /run/pulse/native ] && echo yes || echo no)"
        ls -la /run/pulse/ 2>/dev/null || echo "  /run/pulse directory empty"
        echo ""
        echo "Check that /dev/snd is mounted and accessible"
        exit 1
    fi
    if [ $((WAIT_COUNT % 5)) -eq 0 ]; then
        echo "Waiting for PulseAudio... (${WAIT_COUNT}s)"
    fi
    sleep 1
done

echo "PulseAudio is ready!"
echo ""

# In Docker, udev doesn't work so module-udev-detect won't find devices.
# Manually detect ALSA cards and load them into PulseAudio.
echo "Detecting ALSA audio devices..."

# Debug: show what's available
echo "  Checking /dev/snd:"
ls -la /dev/snd/ 2>/dev/null || echo "    /dev/snd NOT MOUNTED"
echo ""

CARDS_LOADED=0

if [ -f /proc/asound/cards ]; then
    echo "  Reading from /proc/asound/cards:"
    cat /proc/asound/cards
    echo ""

    # Parse card numbers from /proc/asound/cards
    # Format: " 0 [USB            ]: USB-Audio - USB Audio Device"
    grep -E '^ *[0-9]+' /proc/asound/cards | while read -r line; do
        card_num=$(echo "$line" | awk '{print $1}')
        card_name=$(echo "$line" | sed 's/.*\]: //')

        echo "  Loading ALSA card $card_num: $card_name"

        # Load the card into PulseAudio
        # tsched=0 for better compatibility with USB devices
        if pactl load-module module-alsa-card device=hw:$card_num tsched=0 2>/dev/null; then
            echo "    -> Loaded into PulseAudio"
            CARDS_LOADED=$((CARDS_LOADED + 1))
        else
            echo "    -> Failed to load (may not support playback)"
        fi
    done
elif [ -d /dev/snd ]; then
    # Fallback: detect playback devices from /dev/snd/pcmC*D*p (p = playback)
    echo "  /proc/asound/cards not found, detecting PCM playback devices..."
    for pcm in /dev/snd/pcmC*D*p; do
        if [ -e "$pcm" ]; then
            # Extract card and device from pcmC0D3p -> 0,3
            pcm_name=$(basename "$pcm")
            card_num=$(echo "$pcm_name" | sed 's/pcmC\([0-9]*\)D.*/\1/')
            dev_num=$(echo "$pcm_name" | sed 's/pcmC[0-9]*D\([0-9]*\)p/\1/')

            echo "  Found PCM playback: hw:$card_num,$dev_num ($pcm_name)"

            # Use module-alsa-sink for direct device access (doesn't need /proc/asound)
            sink_name="alsa_output_hw_${card_num}_${dev_num}"

            # Try to load at higher sample rates for better quality
            # Try 192kHz first, then 96kHz, then 48kHz
            LOADED=0
            for rate in 192000 96000 48000; do
                if [ $LOADED -eq 0 ]; then
                    if pactl load-module module-alsa-sink device=hw:$card_num,$dev_num sink_name="$sink_name" rate=$rate tsched=0 2>/dev/null; then
                        echo "    -> Loaded as $sink_name @ ${rate}Hz"
                        CARDS_LOADED=$((CARDS_LOADED + 1))
                        LOADED=1
                    fi
                fi
            done

            if [ $LOADED -eq 0 ]; then
                echo "    -> Failed to load (device may be in use or unsupported)"
            fi
        fi
    done
else
    echo "  ERROR: /dev/snd not mounted!"
    echo ""
    echo "  To use audio devices, run Docker with:"
    echo "    docker run --device /dev/snd -v /proc/asound:/proc/asound:ro ..."
    echo "  Or in docker-compose.yml:"
    echo "    devices:"
    echo "      - /dev/snd:/dev/snd"
    echo "    volumes:"
    echo "      - /proc/asound:/proc/asound:ro"
fi
echo ""

echo "PulseAudio server info:"
pactl info 2>/dev/null | grep -E "^(Server|Default Sink|Default Source)" || true
echo ""
echo "Available PulseAudio sinks:"
pactl list sinks short 2>/dev/null || echo "  (none detected)"
echo ""

echo "========================================="
echo "Starting Multi-Room Audio application"
echo "========================================="

# Run the application - it will use PulseAudio backend
exec ./MultiRoomAudio "$@"
