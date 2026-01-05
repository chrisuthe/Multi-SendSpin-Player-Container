#!/bin/bash
set -e

# Multi-Room Audio Container Entrypoint
# Handles PulseAudio startup for standalone Docker deployments

echo "========================================="
echo "Multi-Room Audio Controller Starting"
echo "========================================="

# Check if we're running in HAOS add-on mode
# HAOS provides PulseAudio via the audio: true config option
if [ -f "/data/options.json" ] || [ -n "$SUPERVISOR_TOKEN" ]; then
    echo "Detected HAOS add-on mode - using system PulseAudio"
    exec ./MultiRoomAudio "$@"
fi

# Check if external PulseAudio is available (socket mounted from host)
if pactl info >/dev/null 2>&1; then
    echo "External PulseAudio detected - using existing server"
    exec ./MultiRoomAudio "$@"
fi

echo "Standalone Docker mode - starting embedded PulseAudio"

# Disable D-Bus integration entirely - we don't have D-Bus in the container
export DBUS_SESSION_BUS_ADDRESS=disabled
export PULSE_SYSTEM=1
# Tell PulseAudio clients where to connect (bypass D-Bus server lookup)
export PULSE_SERVER=unix:/run/pulse/native

# Ensure runtime directory exists and clean up stale files
mkdir -p /run/pulse
chmod 755 /run/pulse

# Clean up stale PID files and locks from previous runs
rm -f /run/pulse/pid /var/run/pulse/pid /var/run/pulse/.pid 2>/dev/null || true
rm -f /run/pulse/*.pid /var/run/pulse/*.pid 2>/dev/null || true
rm -f /tmp/pulse-* /run/pulse/native /var/run/pulse/native 2>/dev/null || true
rm -rf /root/.config/pulse 2>/dev/null || true
pkill -9 pulseaudio 2>/dev/null || true
sleep 0.5

# List available ALSA devices for diagnostics
echo ""
echo "ALSA devices detected:"
if command -v aplay >/dev/null 2>&1; then
    aplay -l 2>/dev/null || echo "  (none found - check /dev/snd mount)"
else
    echo "  (aplay not available)"
fi
echo ""

# Generate dynamic PulseAudio config by reading /proc/asound/cards directly
# (udev doesn't work in Docker containers)
PULSE_CONFIG="/tmp/pulse-dynamic.pa"
echo "Generating PulseAudio config from /proc/asound/cards..."

cat > "$PULSE_CONFIG" << 'EOF'
# Dynamic PulseAudio config - generated at container startup
load-module module-native-protocol-unix auth-anonymous=1
load-module module-device-restore
load-module module-stream-restore
load-module module-card-restore
load-module module-default-device-restore
load-module module-switch-on-connect
EOF

# Load module-alsa-card for each card found in /proc/asound/cards
if [ -f /proc/asound/cards ]; then
    grep -E '^\s*[0-9]+' /proc/asound/cards | while read -r line; do
        card_num=$(echo "$line" | awk '{print $1}')
        card_name=$(echo "$line" | sed -n 's/.*\[\([^]]*\)\].*/\1/p' | tr -d ' ')
        if [ -n "$card_num" ]; then
            echo "Found ALSA card $card_num: $card_name"
            echo ".nofail" >> "$PULSE_CONFIG"
            echo "load-module module-alsa-card device_id=$card_num tsched=1" >> "$PULSE_CONFIG"
            echo ".fail" >> "$PULSE_CONFIG"
        fi
    done
fi

echo "load-module module-always-sink" >> "$PULSE_CONFIG"

echo ""
echo "Generated PulseAudio config:"
cat "$PULSE_CONFIG"
echo ""

# Start PulseAudio with our dynamic config
echo "Starting PulseAudio daemon..."
pulseaudio \
    --system \
    --disallow-exit \
    --disallow-module-loading=false \
    --daemonize=false \
    --use-pid-file=false \
    --log-target=stderr \
    --log-level=notice \
    --file="$PULSE_CONFIG" &

PA_PID=$!

# Wait for PulseAudio to be ready
echo "Waiting for PulseAudio to initialize..."
for i in $(seq 1 30); do
    if pactl info >/dev/null 2>&1; then
        echo "PulseAudio ready!"
        break
    fi
    if ! kill -0 $PA_PID 2>/dev/null; then
        echo "ERROR: PulseAudio daemon exited unexpectedly"
        exit 1
    fi
    sleep 0.5
done

# Final check
if ! pactl info >/dev/null 2>&1; then
    echo "ERROR: PulseAudio failed to start within timeout"
    exit 1
fi

# Show PulseAudio info
echo ""
echo "PulseAudio server info:"
pactl info 2>/dev/null | grep -E "^(Server|Default Sink|Default Source)" || true
echo ""

# List available sinks
echo "Available audio sinks:"
pactl list sinks short 2>/dev/null || echo "  (none)"

# If no real sinks detected, manually load ALSA sinks
SINK_COUNT=$(pactl list sinks short 2>/dev/null | grep -v "module-null-sink" | wc -l)
if [ "$SINK_COUNT" -eq 0 ]; then
    echo ""
    echo "No sinks auto-detected, manually loading ALSA devices..."

    # Parse aplay -l output and load sinks for each playback device
    aplay -l 2>/dev/null | grep "^card" | while read line; do
        CARD=$(echo "$line" | sed -n 's/^card \([0-9]*\):.*/\1/p')
        DEVICE=$(echo "$line" | sed -n 's/.*device \([0-9]*\):.*/\1/p')
        NAME=$(echo "$line" | sed -n 's/^card [0-9]*: \([^,]*\).*/\1/p' | tr ' ' '_')

        if [ -n "$CARD" ] && [ -n "$DEVICE" ]; then
            SINK_NAME="alsa_output.hw_${CARD}_${DEVICE}"
            echo "  Loading sink for hw:${CARD},${DEVICE} (${NAME})..."
            pactl load-module module-alsa-sink device="hw:${CARD},${DEVICE}" sink_name="${SINK_NAME}" sink_properties="device.description='${NAME}'" 2>/dev/null || \
                echo "    Warning: Failed to load hw:${CARD},${DEVICE}"
        fi
    done

    echo ""
    echo "Sinks after manual loading:"
    pactl list sinks short 2>/dev/null || echo "  (none)"
fi
echo ""

echo "========================================="
echo "Starting Multi-Room Audio application"
echo "========================================="

# Run the main application
# Use exec to replace this shell, but we need to handle PulseAudio shutdown
# So we trap signals and forward them
trap "kill $PA_PID 2>/dev/null; exit" SIGTERM SIGINT

./MultiRoomAudio "$@" &
APP_PID=$!

# Wait for either process to exit
wait $APP_PID
APP_EXIT=$?

# Cleanup
kill $PA_PID 2>/dev/null || true

exit $APP_EXIT
