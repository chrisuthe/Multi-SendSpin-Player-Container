#!/usr/bin/env python3
"""
Common functionality shared between app.py and app_enhanced.py.

This module contains:
- Flask and SocketIO initialization
- All HTTP routes (players, devices, volume, debug, state)
- WebSocket handlers
- Status monitoring thread
- Server startup logic

Both app.py and app_enhanced.py import from this module to avoid code duplication.
The key difference is the manager implementation they use.
"""

import logging
import os
import re
import secrets
import subprocess
import sys
import threading
import time
import traceback

from environment import is_hassio
from flask import Flask, jsonify, render_template, request, send_from_directory
from flask_socketio import SocketIO, emit
from flask_swagger_ui import get_swaggerui_blueprint
from schemas.player_config import INVALID_NAME_CHARS, MAX_NAME_LENGTH

logger = logging.getLogger(__name__)

# =============================================================================
# CONSTANTS
# =============================================================================

# How often to poll player statuses and emit WebSocket updates
STATUS_MONITOR_INTERVAL_SECS = 2

# Delay before retrying after an error in status monitor loop
STATUS_MONITOR_ERROR_DELAY_SECS = 5

# =============================================================================
# REGEX PATTERNS
# =============================================================================

# Parses sendspin --list-audio-devices output to extract device index and name.
# Matches: "[0] HDA NVidia: HDMI 0 (hw:1,3) (default)" -> ("0", "HDA NVidia: HDMI 0 (hw:1,3) (default)")
# Format: Square brackets containing device index, followed by whitespace and device name.
# Group 1: Device index (integer as string)
# Group 2: Device name (everything after the index)
# Used by: get_portaudio_devices() endpoint
SENDSPIN_DEVICE_PATTERN = re.compile(r"^\[(\d+)\]\s*(.+)$")

# =============================================================================
# FLASK INITIALIZATION
# =============================================================================


def create_flask_app():
    """
    Create and configure the Flask application with SocketIO and Swagger.

    Returns:
        Tuple of (Flask app, SocketIO instance).
    """
    try:
        app = Flask(__name__)
        secret_key = os.environ.get("SECRET_KEY")
        if not secret_key:
            secret_key = secrets.token_hex(32)
            logger.warning(
                "SECRET_KEY not set - using randomly generated key. "
                "This is not suitable for production. Set SECRET_KEY environment variable."
            )
        app.config["SECRET_KEY"] = secret_key
        socketio = SocketIO(app, cors_allowed_origins="*")

        # Configure Swagger UI (use /docs to avoid conflicts with /api/* routes through Ingress)
        SWAGGER_URL = "/docs"
        API_URL = "/api/swagger.yaml"

        swaggerui_blueprint = get_swaggerui_blueprint(
            SWAGGER_URL,
            API_URL,
            config={
                "app_name": "Multi Output Player API",
                "layout": "BaseLayout",
                "deepLinking": True,
                "showExtensions": True,
                "showCommonExtensions": True,
            },
        )

        app.register_blueprint(swaggerui_blueprint)

        # Add CORS headers to all responses for cross-origin access
        # This is needed when accessing the add-on directly vs through Ingress
        @app.after_request
        def add_cors_headers(response):
            response.headers["Access-Control-Allow-Origin"] = "*"
            response.headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS"
            response.headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization"
            return response

        # Handle OPTIONS preflight requests
        @app.before_request
        def handle_preflight():
            if request.method == "OPTIONS":
                response = app.make_default_options_response()
                response.headers["Access-Control-Allow-Origin"] = "*"
                response.headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS"
                response.headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization"
                return response

        # Global error handler to return JSON for all 500 errors
        @app.errorhandler(500)
        def handle_internal_error(error):
            """Return JSON for internal server errors instead of HTML"""
            logger.error(f"Internal server error: {error}")
            return jsonify({"success": False, "error": "Internal server error", "details": str(error)}), 500

        @app.errorhandler(Exception)
        def handle_exception(error):
            """Catch-all exception handler to return JSON"""
            logger.error(f"Unhandled exception: {type(error).__name__}: {error}")
            return jsonify({"success": False, "error": str(error)}), 500

        logger.info("Flask app, SocketIO, and Swagger UI initialized successfully")
        return app, socketio

    except Exception as e:
        logger.error(f"Failed to initialize Flask app: {e}")
        traceback.print_exc()
        sys.exit(1)


# =============================================================================
# ROUTE REGISTRATION
# =============================================================================


def register_routes(app, manager):
    """
    Register all Flask HTTP routes.

    Args:
        app: Flask application instance.
        manager: Player manager instance (PlayerManager or SqueezeliteManager).
    """

    @app.route("/")
    def index():
        """Main page showing all players"""
        players = manager.players
        statuses = manager.get_all_statuses()
        devices = manager.get_audio_devices()
        # Build device ID -> name lookup for display in status cards
        device_names = {d["id"]: d["name"] for d in devices}
        return render_template(
            "index.html", players=players, statuses=statuses, devices=devices, device_names=device_names
        )

    @app.route("/api/swagger.yaml")
    def swagger_yaml():
        """Serve the Swagger YAML specification"""
        try:
            return send_from_directory("/app", "swagger.yaml")
        except Exception as e:
            logger.error(f"Error serving swagger.yaml: {e}")
            return jsonify({"error": "Swagger specification not found"}), 404

    @app.route("/api/players", methods=["GET"])
    def get_players():
        """API endpoint to get all players"""
        return jsonify({"players": manager.players, "statuses": manager.get_all_statuses()})

    @app.route("/api/devices", methods=["GET"])
    def get_devices():
        """API endpoint to get audio devices (ALSA)"""
        try:
            devices = manager.get_audio_devices()
            logger.info(f"GET /api/devices returning {len(devices)} devices")
            return jsonify({"devices": devices})
        except Exception as e:
            logger.error(f"Error in get_devices: {e}")
            return jsonify({"devices": [], "error": str(e)}), 500

    @app.route("/api/devices/portaudio", methods=["GET"])
    def get_portaudio_devices():
        """API endpoint to get PortAudio devices (for Sendspin)"""
        try:
            result = subprocess.run(
                ["sendspin", "--list-audio-devices"],
                capture_output=True,
                text=True,
                timeout=10,
            )

            # Log both stdout and stderr for debugging
            logger.debug(f"sendspin --list-audio-devices stdout: {result.stdout}")
            if result.stderr:
                logger.warning(f"sendspin --list-audio-devices stderr: {result.stderr}")

            # Parse the output - sendspin lists devices as "[0] Device Name"
            devices = []
            for line in result.stdout.strip().split("\n"):
                line = line.strip()
                match = SENDSPIN_DEVICE_PATTERN.match(line)
                if match:
                    index = match.group(1)
                    name = match.group(2)
                    devices.append({"index": index, "name": name, "raw": line})

            # Also check stderr for device listings (some versions output there)
            if not devices and result.stderr:
                for line in result.stderr.strip().split("\n"):
                    line = line.strip()
                    match = SENDSPIN_DEVICE_PATTERN.match(line)
                    if match:
                        index = match.group(1)
                        name = match.group(2)
                        devices.append({"index": index, "name": name, "raw": line})

            # On HAOS, if PortAudio found no devices, fall back to PulseAudio sinks
            if not devices and is_hassio():
                logger.info("No PortAudio devices found on HAOS, falling back to PulseAudio sinks")
                try:
                    pactl_result = subprocess.run(
                        ["pactl", "list", "sinks", "short"],
                        capture_output=True,
                        text=True,
                        timeout=10,
                    )
                    # Parse PulseAudio sinks: "0\talsa_output.pci-0000_00_1f.3.analog-stereo\t..."
                    for line in pactl_result.stdout.strip().split("\n"):
                        if not line.strip():
                            continue
                        parts = line.split("\t")
                        if len(parts) >= 2:
                            sink_index = parts[0]
                            sink_name = parts[1]
                            # Use sink name as the device identifier for sendspin
                            devices.append({"index": sink_index, "name": sink_name, "raw": line, "type": "pulseaudio"})
                    if devices:
                        logger.info(f"Found {len(devices)} PulseAudio sinks as fallback")
                except Exception as e:
                    logger.warning(f"Failed to get PulseAudio sinks as fallback: {e}")

            response = {
                "success": True,
                "devices": devices,
                "raw_output": result.stdout,
                "note": "Use device index (0, 1, 2) with --audio-device for sendspin",
            }

            # Update note if we used PulseAudio fallback
            if devices and any(d.get("type") == "pulseaudio" for d in devices):
                response["note"] = "Using PulseAudio sinks. Use sink name with sendspin on HAOS."
                response["fallback"] = "pulseaudio"

            # Include stderr info if there were issues but we still got some output
            if result.stderr and not devices:
                response["stderr"] = result.stderr
                response["success"] = len(devices) > 0
                if not devices:
                    response["message"] = "No devices found. Check stderr for errors."

            return jsonify(response)
        except FileNotFoundError:
            logger.error("sendspin binary not found")
            return jsonify(
                {
                    "success": False,
                    "message": "sendspin binary not found",
                    "devices": [],
                }
            )
        except subprocess.TimeoutExpired:
            logger.error("Timeout running sendspin --list-audio-devices")
            return jsonify(
                {
                    "success": False,
                    "message": "Timeout listing audio devices",
                    "devices": [],
                }
            )
        except Exception as e:
            logger.error(f"Error running sendspin --list-audio-devices: {e}")
            return jsonify(
                {
                    "success": False,
                    "message": str(e),
                    "devices": [],
                }
            )

    @app.route("/api/devices/test", methods=["POST"])
    def test_audio_device():
        """API endpoint to play a test tone on an audio device"""
        try:
            data = request.json or {}
            device = data.get("device")

            if not device:
                return jsonify({"success": False, "message": "Device is required"}), 400

            # Check if manager has audio manager with test tone capability
            if hasattr(manager, "audio") and hasattr(manager.audio, "play_test_tone"):
                success, message = manager.audio.play_test_tone(device)
                return jsonify({"success": success, "message": message})
            else:
                return jsonify({"success": False, "message": "Test tone not available in this version"}), 501
        except Exception as e:
            logger.error(f"Error in test_audio_device: {e}")
            return jsonify({"success": False, "message": f"Error: {str(e)}"}), 500

    @app.route("/api/providers", methods=["GET"])
    def get_providers():
        """API endpoint to get available player providers"""
        try:
            # Check if manager has get_available_providers method (PlayerManager)
            if hasattr(manager, "get_available_providers"):
                providers = manager.get_available_providers()
                logger.info(f"GET /api/providers found {len(providers)} available providers")
                # If no providers are available, log a warning and return all registered providers
                if not providers and hasattr(manager, "providers"):
                    logger.warning("No available providers found, returning all registered providers")
                    providers = manager.providers.get_provider_info(available_only=False)
                    logger.info(f"Returning {len(providers)} total registered providers")
                return jsonify({"providers": providers})
            else:
                # For SqueezeliteManager, return default squeezelite provider
                return jsonify(
                    {
                        "providers": [
                            {
                                "type": "squeezelite",
                                "name": "Squeezelite",
                                "description": "Logitech Media Server compatible player",
                                "available": True,
                            }
                        ]
                    }
                )
        except Exception as e:
            logger.error(f"Error in get_providers: {e}")
            return jsonify({"success": False, "error": str(e), "providers": []}), 500

    @app.route("/api/players", methods=["POST"])
    def create_player():
        """API endpoint to create a new player"""
        logger.info("POST /api/players - create_player endpoint called")
        import sys

        sys.stdout.flush()
        # Validate request.json is present and valid
        if request.json is None:
            return jsonify({"success": False, "error": "Request body must be valid JSON"}), 400

        data = request.json
        name = data.get("name")
        device = data.get("device", "default")
        logger.info(f"POST /api/players - name={name}, device={device}, provider={data.get('provider', 'squeezelite')}")

        if not name:
            return jsonify({"success": False, "error": "Name is required"}), 400

        # Validate player name format
        if len(name) > MAX_NAME_LENGTH:
            return jsonify({"success": False, "error": f"Name must be at most {MAX_NAME_LENGTH} characters"}), 400

        invalid_found = set(name) & INVALID_NAME_CHARS
        if invalid_found:
            chars_repr = ", ".join(repr(c) for c in invalid_found)
            return jsonify({"success": False, "error": f"Name contains invalid characters: {chars_repr}"}), 400

        # Check if manager uses new PlayerManager or old SqueezeliteManager
        if hasattr(manager, "create_player"):
            # PlayerManager signature: supports provider_type and extra config
            if hasattr(manager, "providers"):
                provider_type = data.get("provider", "squeezelite")
                server_ip = data.get("server_ip", "")
                server_url = data.get("server_url", "")
                mac_address = data.get("mac_address", "")

                # Extract any extra provider-specific config
                extra_config = {
                    k: v
                    for k, v in data.items()
                    if k not in ("name", "device", "provider", "server_ip", "server_url", "mac_address")
                }

                logger.info(f"POST /api/players - calling manager.create_player for {provider_type}")
                logger.info(f"POST /api/players - extra_config: {extra_config}")
                import sys

                sys.stdout.flush()
                sys.stderr.flush()
                success, message = manager.create_player(
                    name=name,
                    device=device,
                    provider_type=provider_type,
                    server_ip=server_ip,
                    server_url=server_url,
                    mac_address=mac_address,
                    **extra_config,
                )
            else:
                # SqueezeliteManager signature: simpler parameters
                server_ip = data.get("server_ip", "")
                mac_address = data.get("mac_address", "")
                success, message = manager.create_player(name, device, server_ip, mac_address)
        else:
            return jsonify({"success": False, "error": "Manager does not support player creation"}), 500

        if success:
            return jsonify({"success": True, "message": message})
        else:
            return jsonify({"success": False, "error": message}), 400

    @app.route("/api/players/<name>", methods=["PUT"])
    def update_player(name):
        """API endpoint to update a player"""
        # Validate request.json is present and valid
        if request.json is None:
            return jsonify({"success": False, "error": "Request body must be valid JSON"}), 400

        data = request.json
        new_name = data.get("name", name)
        device = data.get("device", "default")

        # Validate new player name format if provided
        if new_name:
            if len(new_name) > MAX_NAME_LENGTH:
                return jsonify({"success": False, "error": f"Name must be at most {MAX_NAME_LENGTH} characters"}), 400

            invalid_found = set(new_name) & INVALID_NAME_CHARS
            if invalid_found:
                chars_repr = ", ".join(repr(c) for c in invalid_found)
                return jsonify({"success": False, "error": f"Name contains invalid characters: {chars_repr}"}), 400

        # Check if manager uses new PlayerManager or old SqueezeliteManager
        if hasattr(manager, "update_player"):
            if hasattr(manager, "providers"):
                # PlayerManager signature
                provider_type = data.get("provider")
                server_ip = data.get("server_ip", "")
                server_url = data.get("server_url", "")
                mac_address = data.get("mac_address", "")

                extra_config = {
                    k: v
                    for k, v in data.items()
                    if k not in ("name", "device", "provider", "server_ip", "server_url", "mac_address")
                }

                success, message = manager.update_player(
                    old_name=name,
                    new_name=new_name,
                    device=device,
                    provider_type=provider_type,
                    server_ip=server_ip,
                    server_url=server_url,
                    mac_address=mac_address,
                    **extra_config,
                )
            else:
                # SqueezeliteManager signature
                server_ip = data.get("server_ip", "")
                mac_address = data.get("mac_address", "")
                success, message = manager.update_player(name, new_name, device, server_ip, mac_address)
        else:
            return jsonify({"success": False, "error": "Manager does not support player updates"}), 500

        if success:
            return jsonify({"success": True, "message": message, "data": {"new_name": new_name}})
        else:
            return jsonify({"success": False, "error": message}), 400

    @app.route("/api/players/<name>", methods=["DELETE"])
    def delete_player(name):
        """API endpoint to delete a player"""
        success, message = manager.delete_player(name)
        if success:
            return jsonify({"success": True, "message": message})
        else:
            return jsonify({"success": False, "error": message}), 404

    @app.route("/api/players/<name>", methods=["GET"])
    def get_player(name):
        """API endpoint to get a single player's configuration"""
        player_config = manager.config.get_player(name)
        if player_config is None:
            return jsonify({"success": False, "error": "Player not found"}), 404
        return jsonify(player_config)

    @app.route("/api/players/<name>/start", methods=["POST"])
    def start_player(name):
        """API endpoint to start a player"""
        success, message = manager.start_player(name)
        return jsonify({"success": success, "message": message})

    @app.route("/api/players/<name>/stop", methods=["POST"])
    def stop_player(name):
        """API endpoint to stop a player"""
        success, message = manager.stop_player(name)
        return jsonify({"success": success, "message": message})

    @app.route("/api/players/<name>/status", methods=["GET"])
    def get_player_status(name):
        """API endpoint to get player status"""
        status = manager.get_player_status(name)
        return jsonify({"running": status})

    @app.route("/api/players/<n>/volume", methods=["GET"])
    def get_player_volume(n):
        """API endpoint to get player volume"""
        try:
            volume = manager.get_player_volume(n)
            if volume is None:
                return jsonify({"success": False, "message": "Player not found"}), 404
            return jsonify({"success": True, "volume": volume})
        except Exception as e:
            logger.error(f"Error in get_player_volume for {n}: {e}")
            return jsonify({"success": False, "message": f"Server error: {str(e)}"}), 500

    @app.route("/api/players/<n>/volume", methods=["POST"])
    def set_player_volume(n):
        """API endpoint to set player volume"""
        try:
            data = request.json
            volume = data.get("volume")

            if volume is None:
                return jsonify({"success": False, "message": "Volume is required"}), 400

            try:
                volume = int(volume)
            except (ValueError, TypeError):
                return jsonify({"success": False, "message": "Volume must be a number"}), 400

            success, message = manager.set_player_volume(n, volume)
            return jsonify({"success": success, "message": message})
        except Exception as e:
            logger.error(f"Error in set_player_volume for {n}: {e}")
            logger.error(f"Request data: {request.get_data().decode('utf-8', errors='replace')}")
            return jsonify({"success": False, "message": f"Server error: {str(e)}"}), 500

    @app.route("/api/players/<n>/offset", methods=["PUT"])
    def update_player_offset(n):
        """
        API endpoint to update player sync offset (delay_ms).

        The offset is saved to config but requires a player restart to apply.
        Typical values are negative (-100 to -200ms) to compensate for audio latency.

        Request body:
            {"delay_ms": -150}

        Returns:
            {"success": true, "delay_ms": -150, "restart_required": true, "message": "..."}
        """
        try:
            data = request.json
            delay_ms = data.get("delay_ms", 0)

            # Validate delay_ms is an integer
            try:
                delay_ms = int(delay_ms)
            except (ValueError, TypeError):
                return jsonify({"success": False, "message": "delay_ms must be an integer"}), 400

            # Validate range
            if delay_ms < -1000 or delay_ms > 1000:
                return (
                    jsonify({"success": False, "message": "delay_ms must be between -1000 and 1000"}),
                    400,
                )

            # Update the config
            success = manager.config.update_player_field(n, "delay_ms", delay_ms)
            if not success:
                return jsonify({"success": False, "message": "Player not found"}), 404

            return jsonify(
                {
                    "success": True,
                    "delay_ms": delay_ms,
                    "restart_required": True,
                    "message": f"Offset updated to {delay_ms}ms. Restart player to apply.",
                }
            )
        except Exception as e:
            logger.error(f"Error in update_player_offset for {n}: {e}")
            return jsonify({"success": False, "message": f"Server error: {str(e)}"}), 500

    @app.route("/api/debug/audio", methods=["GET"])
    def debug_audio():
        """Debug endpoint to check audio device detection"""
        try:
            WINDOWS_MODE = os.environ.get("SQUEEZELITE_WINDOWS_MODE", "0") == "1"
            debug_info = {
                "container_mode": WINDOWS_MODE,
                "detected_devices": manager.get_audio_devices(),
                "aplay_available": False,
                "amixer_available": False,
                "aplay_output": "",
                "amixer_cards_output": "",
                "mixer_controls": {},
            }

            # Test aplay command
            try:
                result = subprocess.run(["aplay", "-l"], capture_output=True, text=True, check=True)
                debug_info["aplay_available"] = True
                debug_info["aplay_output"] = result.stdout
            except (subprocess.CalledProcessError, FileNotFoundError) as e:
                debug_info["aplay_output"] = str(e)

            # Test amixer command
            try:
                result = subprocess.run(["amixer"], capture_output=True, text=True, check=True)
                debug_info["amixer_available"] = True
                debug_info["amixer_cards_output"] = result.stdout
            except (subprocess.CalledProcessError, FileNotFoundError) as e:
                debug_info["amixer_cards_output"] = str(e)

            # Test mixer controls for each detected hardware device
            if hasattr(manager, "get_mixer_controls"):
                for device in debug_info["detected_devices"]:
                    if device["id"].startswith("hw:"):
                        device_id = device["id"]
                        try:
                            controls = manager.get_mixer_controls(device_id)
                            debug_info["mixer_controls"][device_id] = controls
                        except Exception as e:
                            debug_info["mixer_controls"][device_id] = f"Error: {e}"

            return jsonify(debug_info)
        except Exception as e:
            logger.error(f"Error in debug_audio: {e}")
            return jsonify({"error": str(e)}), 500

    # State management endpoints (only for enhanced version with SqueezeliteManager)
    @app.route("/api/state", methods=["GET"])
    def get_state():
        """API endpoint to get state information"""
        if hasattr(manager, "get_state_info"):
            return jsonify(manager.get_state_info())
        else:
            return jsonify({"message": "State persistence not available in this version"}), 404

    @app.route("/api/state/save", methods=["POST"])
    def save_state():
        """API endpoint to manually save current state"""
        if hasattr(manager, "save_state"):
            try:
                manager.save_state()
                return jsonify({"success": True, "message": "State saved successfully"})
            except Exception as e:
                return jsonify({"success": False, "message": f"Error saving state: {e}"}), 500
        else:
            return jsonify({"success": False, "message": "State persistence not available in this version"}), 404


# =============================================================================
# WEBSOCKET HANDLERS
# =============================================================================

# Default timeout for WebSocket emit operations (in seconds)
WEBSOCKET_EMIT_TIMEOUT_SECS = 5

# Counter for tracking consecutive WebSocket failures (for adaptive logging)
_websocket_failure_count = 0
_websocket_failure_count_lock = threading.Lock()


def safe_emit(socketio, event, data, timeout=WEBSOCKET_EMIT_TIMEOUT_SECS):
    """
    Safely emit a WebSocket event with error handling and logging.

    This function wraps socketio.emit() with proper exception handling to
    prevent WebSocket errors from crashing the status monitor thread or
    other background processes.

    Args:
        socketio: SocketIO instance.
        event: Event name to emit.
        data: Data to send with the event.
        timeout: Timeout in seconds (note: Flask-SocketIO's emit is non-blocking
                 by default; this is used for logging/monitoring purposes).

    Returns:
        bool: True if emit succeeded, False otherwise.
    """
    global _websocket_failure_count

    try:
        socketio.emit(event, data)

        # Reset failure count on success
        with _websocket_failure_count_lock:
            if _websocket_failure_count > 0:
                logger.info(f"WebSocket emit recovered after {_websocket_failure_count} failures")
                _websocket_failure_count = 0

        return True

    except ConnectionError as e:
        with _websocket_failure_count_lock:
            _websocket_failure_count += 1
            # Log every failure for the first 3, then every 10th to avoid log spam
            if _websocket_failure_count <= 3 or _websocket_failure_count % 10 == 0:
                logger.warning(
                    f"WebSocket connection error for event '{event}' (failure #{_websocket_failure_count}): {e}"
                )
        return False

    except BrokenPipeError as e:
        with _websocket_failure_count_lock:
            _websocket_failure_count += 1
            if _websocket_failure_count <= 3 or _websocket_failure_count % 10 == 0:
                logger.warning(f"WebSocket broken pipe for event '{event}' (failure #{_websocket_failure_count}): {e}")
        return False

    except OSError as e:
        # Catch socket-related OS errors (e.g., connection reset)
        with _websocket_failure_count_lock:
            _websocket_failure_count += 1
            if _websocket_failure_count <= 3 or _websocket_failure_count % 10 == 0:
                logger.warning(f"WebSocket OS error for event '{event}' (failure #{_websocket_failure_count}): {e}")
        return False

    except Exception as e:
        with _websocket_failure_count_lock:
            _websocket_failure_count += 1
        logger.error(f"Unexpected WebSocket error for event '{event}': {type(e).__name__}: {e}")
        return False


def register_websocket_handlers(socketio, manager):
    """
    Register WebSocket event handlers.

    Args:
        socketio: SocketIO instance.
        manager: Player manager instance.
    """

    @socketio.on("connect")
    def handle_connect():
        """Handle WebSocket client connection"""
        logger.debug("WebSocket client connected")
        try:
            emit("status_update", manager.get_all_statuses())
        except Exception as e:
            logger.warning(f"Failed to send initial status update on connect: {e}")

    @socketio.on("disconnect")
    def handle_disconnect(reason=None):
        """Handle WebSocket client disconnection gracefully"""
        logger.debug(f"WebSocket client disconnected (reason: {reason})")

    @socketio.on_error_default
    def default_error_handler(e):
        """
        Handle WebSocket errors globally.

        This catches any unhandled exceptions in WebSocket event handlers
        and logs them appropriately without crashing the server.
        """
        logger.error(f"WebSocket error: {type(e).__name__}: {e}")


def start_status_monitor(socketio, manager):
    """
    Start the background thread that monitors player statuses.

    Args:
        socketio: SocketIO instance for emitting updates.
        manager: Player manager instance.

    Returns:
        threading.Thread instance (already started).
    """

    def status_monitor():
        """Background thread to monitor player statuses"""
        logger.info("Starting status monitor thread")
        while True:
            try:
                statuses = manager.get_all_statuses()
                # Use safe_emit to handle WebSocket errors gracefully
                safe_emit(socketio, "status_update", statuses)
                time.sleep(STATUS_MONITOR_INTERVAL_SECS)
            except Exception as e:
                # This catches errors in get_all_statuses() or other non-WebSocket issues
                logger.error(f"Error in status monitor: {e}")
                time.sleep(STATUS_MONITOR_ERROR_DELAY_SECS)

    try:
        logger.info("Starting status monitoring thread...")
        status_thread = threading.Thread(target=status_monitor, daemon=True)
        status_thread.start()
        logger.info("Status monitoring thread started successfully")
        return status_thread
    except Exception as e:
        logger.error(f"Failed to start status monitoring thread: {e}")
        return None


# =============================================================================
# SERVER STARTUP
# =============================================================================


def run_server(app, socketio, host="0.0.0.0", port=8080):
    """
    Start the Flask-SocketIO server.

    Args:
        app: Flask application instance.
        socketio: SocketIO instance.
        host: Host address to bind to.
        port: Port number to bind to.
    """
    try:
        logger.info("Starting Flask-SocketIO server...")
        logger.info(f"Server will be available at: http://{host}:{port}")

        # Test if port is available
        import socket

        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        result = sock.connect_ex(("localhost", port))
        if result == 0:
            logger.warning(f"Port {port} appears to be in use, but will try to bind anyway")
        sock.close()

        socketio.run(app, host=host, port=port, debug=False, allow_unsafe_werkzeug=True)
    except Exception as e:
        logger.error(f"Failed to start Flask-SocketIO server: {e}")
        traceback.print_exc()
        sys.exit(1)
