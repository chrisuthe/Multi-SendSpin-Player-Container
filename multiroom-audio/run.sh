#!/usr/bin/env bashio
# Home Assistant Add-on startup script
# Reads configuration from /data/options.json and starts the application

set -e

# Ensure PATH includes all common binary locations
# pip on Alpine may install to /usr/local/bin or Python-specific paths
export PATH="/usr/local/bin:/usr/bin:/bin:/usr/local/sbin:/usr/sbin:/sbin:$PATH"

# Read options from Home Assistant add-on config
CONFIG_PATH="/data/options.json"

if [ -f "$CONFIG_PATH" ]; then
    LOG_LEVEL=$(bashio::config 'log_level' 'info')
else
    LOG_LEVEL="info"
fi

# Export environment variables for the Python app
export LOG_LEVEL="${LOG_LEVEL}"
export CONFIG_PATH="/data"
export LOG_PATH="/share/multiroom-audio/logs"
export AUDIO_BACKEND="pulse"
export WEB_PORT="8096"
export FLASK_ENV="production"

# PulseAudio configuration for HAOS
# Note: When audio: true is set in config.yaml, HAOS supervisor provides
# PULSE_SERVER and other PulseAudio env vars automatically.
# Only set defaults if not already provided by the supervisor.
if [ -z "${PULSE_SERVER:-}" ]; then
    # Fallback for non-HAOS environments or older HAOS versions
    export PULSE_SERVER="unix:/run/pulse/native"
fi

# Ensure directories exist
mkdir -p /share/multiroom-audio/logs

# Log startup information
bashio::log.info "Starting Multi-Room Audio Controller..."
bashio::log.info "Log level: ${LOG_LEVEL}"
bashio::log.info "Audio backend: pulse (HAOS)"

# List available audio devices
bashio::log.info "Detecting audio devices..."
bashio::log.info "PULSE_SERVER: ${PULSE_SERVER:-not set}"

# Verify ALSA-PulseAudio configuration
if [ -f /etc/asound.conf ]; then
    bashio::log.info "ALSA configured to route through PulseAudio"
fi

if command -v pactl &> /dev/null; then
    bashio::log.info "PulseAudio sinks:"
    pactl list sinks short 2>/dev/null || bashio::log.warning "Could not list PulseAudio sinks"

    # Also test PortAudio device detection for Sendspin
    # With ALSA-PulseAudio bridge, PortAudio should now see PulseAudio devices
    if command -v sendspin &> /dev/null; then
        bashio::log.info "PortAudio devices (for Sendspin via ALSA-PulseAudio bridge):"
        sendspin --list-audio-devices 2>&1 || bashio::log.warning "Could not list PortAudio devices"
    fi
fi

# Check for player binaries and log their locations
bashio::log.info "Checking player binaries..."
bashio::log.info "PATH: ${PATH}"

SQUEEZELITE_PATH=$(which squeezelite 2>/dev/null || true)
if [ -n "$SQUEEZELITE_PATH" ]; then
    bashio::log.info "✓ squeezelite found at: ${SQUEEZELITE_PATH}"
else
    bashio::log.warning "✗ squeezelite not found in PATH"
    # Check common locations manually
    for loc in /usr/bin/squeezelite /usr/local/bin/squeezelite; do
        if [ -x "$loc" ]; then
            bashio::log.info "  Found at $loc (adding to PATH)"
            export PATH="$(dirname $loc):$PATH"
        fi
    done
fi

SNAPCLIENT_PATH=$(which snapclient 2>/dev/null || true)
if [ -n "$SNAPCLIENT_PATH" ]; then
    bashio::log.info "✓ snapclient found at: ${SNAPCLIENT_PATH}"
else
    bashio::log.warning "✗ snapclient not found (optional)"
fi

SENDSPIN_PATH=$(which sendspin 2>/dev/null || true)
if [ -n "$SENDSPIN_PATH" ]; then
    bashio::log.info "✓ sendspin found at: ${SENDSPIN_PATH}"
else
    bashio::log.warning "✗ sendspin not found in PATH"
    # Check Python script locations
    for loc in /usr/bin/sendspin /usr/local/bin/sendspin; do
        if [ -x "$loc" ]; then
            bashio::log.info "  Found at $loc (adding to PATH)"
            export PATH="$(dirname $loc):$PATH"
        fi
    done
fi

bashio::log.info "Final PATH: ${PATH}"

# Start the Flask application
bashio::log.info "Starting web interface on port 8096..."
cd /app

# Use Flask's built-in server directly
# Note: We avoid gunicorn due to fork-related segfaults with SocketIO and audio libraries.
# Flask's dev server is sufficient for home automation with limited concurrent users.
exec python3 app.py
