"""
Integration tests for Flask API endpoints.

Tests HTTP endpoints defined in app/common.py for player management
operations using Flask test client with mocked manager methods.
"""

# Import route registration function
import sys
from pathlib import Path
from unittest.mock import Mock, patch

import pytest
from flask import Flask

app_dir = Path(__file__).parent.parent / "app"
sys.path.insert(0, str(app_dir))

from common import register_routes  # noqa: E402

# =============================================================================
# FIXTURES
# =============================================================================


@pytest.fixture
def mock_manager():
    """Create a mock manager with PlayerManager-like interface."""
    manager = Mock()
    # Set up default attributes for PlayerManager-style manager
    manager.players = {}
    manager.providers = {"squeezelite": Mock(), "sendspin": Mock()}
    return manager


@pytest.fixture
def mock_squeezelite_manager():
    """Create a mock manager with SqueezeliteManager-like interface (no providers)."""
    manager = Mock()
    manager.players = {}
    # SqueezeliteManager doesn't have providers or get_available_providers
    del manager.providers
    del manager.get_available_providers
    # SqueezeliteManager doesn't have state management methods
    del manager.get_state_info
    del manager.save_state
    return manager


@pytest.fixture
def app(mock_manager):
    """Create Flask test application with registered routes."""
    flask_app = Flask(__name__)
    flask_app.config["TESTING"] = True
    register_routes(flask_app, mock_manager)
    return flask_app


@pytest.fixture
def app_squeezelite(mock_squeezelite_manager):
    """Create Flask test application with SqueezeliteManager."""
    flask_app = Flask(__name__)
    flask_app.config["TESTING"] = True
    register_routes(flask_app, mock_squeezelite_manager)
    return flask_app


@pytest.fixture
def client(app):
    """Create Flask test client."""
    return app.test_client()


@pytest.fixture
def client_squeezelite(app_squeezelite):
    """Create Flask test client for SqueezeliteManager."""
    return app_squeezelite.test_client()


# =============================================================================
# TESTS - POST /api/players (Create Player)
# =============================================================================


class TestCreatePlayer:
    """Tests for POST /api/players endpoint."""

    def test_create_player_valid_player_manager(self, client, mock_manager):
        """Test creating a player with valid data using PlayerManager."""
        mock_manager.create_player.return_value = (True, "Player created successfully")

        response = client.post(
            "/api/players",
            json={
                "name": "Kitchen",
                "device": "hw:0,0",
                "provider": "squeezelite",
                "server_ip": "192.168.1.100",
                "mac_address": "aa:bb:cc:dd:ee:ff",
            },
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["message"] == "Player created successfully"
        mock_manager.create_player.assert_called_once_with(
            name="Kitchen",
            device="hw:0,0",
            provider_type="squeezelite",
            server_ip="192.168.1.100",
            server_url="",
            mac_address="aa:bb:cc:dd:ee:ff",
        )

    def test_create_player_valid_squeezelite_manager(self, client_squeezelite, mock_squeezelite_manager):
        """Test creating a player with valid data using SqueezeliteManager."""
        mock_squeezelite_manager.create_player.return_value = (True, "Player created successfully")

        response = client_squeezelite.post(
            "/api/players",
            json={
                "name": "Kitchen",
                "device": "hw:0,0",
                "server_ip": "192.168.1.100",
                "mac_address": "aa:bb:cc:dd:ee:ff",
            },
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        mock_squeezelite_manager.create_player.assert_called_once_with(
            "Kitchen", "hw:0,0", "192.168.1.100", "aa:bb:cc:dd:ee:ff"
        )

    def test_create_player_missing_name(self, client, mock_manager):
        """Test creating a player without name returns 400."""
        response = client.post(
            "/api/players",
            json={
                "device": "hw:0,0",
                "provider": "squeezelite",
            },
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "name is required" in data["error"].lower()
        mock_manager.create_player.assert_not_called()

    def test_create_player_empty_name(self, client, mock_manager):
        """Test creating a player with empty name returns 400."""
        response = client.post(
            "/api/players",
            json={
                "name": "",
                "device": "hw:0,0",
            },
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "name is required" in data["error"].lower()

    def test_create_player_default_device(self, client, mock_manager):
        """Test creating a player uses default device when not specified."""
        mock_manager.create_player.return_value = (True, "Player created")

        response = client.post(
            "/api/players",
            json={"name": "Kitchen"},
        )

        assert response.status_code == 200
        call_kwargs = mock_manager.create_player.call_args[1]
        assert call_kwargs["device"] == "default"

    def test_create_player_default_provider(self, client, mock_manager):
        """Test creating a player uses squeezelite provider by default."""
        mock_manager.create_player.return_value = (True, "Player created")

        response = client.post(
            "/api/players",
            json={"name": "Kitchen"},
        )

        assert response.status_code == 200
        call_kwargs = mock_manager.create_player.call_args[1]
        assert call_kwargs["provider_type"] == "squeezelite"

    def test_create_player_sendspin_provider(self, client, mock_manager):
        """Test creating a player with sendspin provider."""
        mock_manager.create_player.return_value = (True, "Sendspin player created")

        response = client.post(
            "/api/players",
            json={
                "name": "Bedroom",
                "device": "0",
                "provider": "sendspin",
                "server_url": "ws://192.168.1.200:8080",
            },
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        call_kwargs = mock_manager.create_player.call_args[1]
        assert call_kwargs["provider_type"] == "sendspin"
        assert call_kwargs["server_url"] == "ws://192.168.1.200:8080"

    def test_create_player_with_extra_config(self, client, mock_manager):
        """Test creating a player passes extra config to manager."""
        mock_manager.create_player.return_value = (True, "Player created")

        response = client.post(
            "/api/players",
            json={
                "name": "Kitchen",
                "device": "hw:0,0",
                "provider": "squeezelite",
                "volume": 75,
                "autostart": True,
            },
        )

        assert response.status_code == 200
        call_kwargs = mock_manager.create_player.call_args[1]
        assert call_kwargs.get("volume") == 75
        assert call_kwargs.get("autostart") is True

    def test_create_player_manager_returns_failure(self, client, mock_manager):
        """Test create player when manager returns failure."""
        mock_manager.create_player.return_value = (False, "Player already exists")

        response = client.post(
            "/api/players",
            json={"name": "Kitchen"},
        )

        assert response.status_code == 400  # API returns 400 for manager failures
        data = response.get_json()
        assert data["success"] is False
        assert data["error"] == "Player already exists"

    def test_create_player_no_create_method(self, client, mock_manager):
        """Test create player when manager lacks create_player method."""
        del mock_manager.create_player

        response = client.post(
            "/api/players",
            json={"name": "Kitchen"},
        )

        assert response.status_code == 500
        data = response.get_json()
        assert data["success"] is False
        assert "does not support player creation" in data["error"].lower()


# =============================================================================
# TESTS - PUT /api/players/<name> (Update Player)
# =============================================================================


class TestUpdatePlayer:
    """Tests for PUT /api/players/<name> endpoint."""

    def test_update_player_valid_player_manager(self, client, mock_manager):
        """Test updating a player with valid data using PlayerManager."""
        mock_manager.update_player.return_value = (True, "Player updated successfully")

        response = client.put(
            "/api/players/Kitchen",
            json={
                "name": "Kitchen2",
                "device": "hw:1,0",
                "provider": "squeezelite",
                "server_ip": "192.168.1.101",
            },
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["data"]["new_name"] == "Kitchen2"
        mock_manager.update_player.assert_called_once_with(
            old_name="Kitchen",
            new_name="Kitchen2",
            device="hw:1,0",
            provider_type="squeezelite",
            server_ip="192.168.1.101",
            server_url="",
            mac_address="",
        )

    def test_update_player_valid_squeezelite_manager(self, client_squeezelite, mock_squeezelite_manager):
        """Test updating a player with valid data using SqueezeliteManager."""
        mock_squeezelite_manager.update_player.return_value = (True, "Player updated successfully")

        response = client_squeezelite.put(
            "/api/players/Kitchen",
            json={
                "name": "Kitchen2",
                "device": "hw:1,0",
                "server_ip": "192.168.1.101",
                "mac_address": "aa:bb:cc:dd:ee:ff",
            },
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        mock_squeezelite_manager.update_player.assert_called_once_with(
            "Kitchen", "Kitchen2", "hw:1,0", "192.168.1.101", "aa:bb:cc:dd:ee:ff"
        )

    def test_update_player_keep_same_name(self, client, mock_manager):
        """Test updating player configuration without changing name."""
        mock_manager.update_player.return_value = (True, "Player updated")

        response = client.put(
            "/api/players/Kitchen",
            json={
                "device": "hw:2,0",
            },
        )

        assert response.status_code == 200
        call_kwargs = mock_manager.update_player.call_args[1]
        assert call_kwargs["old_name"] == "Kitchen"
        assert call_kwargs["new_name"] == "Kitchen"

    def test_update_player_not_found(self, client, mock_manager):
        """Test updating a non-existent player returns 400."""
        mock_manager.update_player.return_value = (False, "Player not found")

        response = client.put(
            "/api/players/NonExistent",
            json={"name": "NewName"},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "not found" in data["error"].lower()

    def test_update_player_default_device(self, client, mock_manager):
        """Test update uses default device when not specified."""
        mock_manager.update_player.return_value = (True, "Updated")

        response = client.put(
            "/api/players/Kitchen",
            json={"name": "Kitchen2"},
        )

        assert response.status_code == 200
        call_kwargs = mock_manager.update_player.call_args[1]
        assert call_kwargs["device"] == "default"

    def test_update_player_with_extra_config(self, client, mock_manager):
        """Test updating a player passes extra config to manager."""
        mock_manager.update_player.return_value = (True, "Updated")

        response = client.put(
            "/api/players/Kitchen",
            json={
                "volume": 50,
                "autostart": False,
            },
        )

        assert response.status_code == 200
        call_kwargs = mock_manager.update_player.call_args[1]
        assert call_kwargs.get("volume") == 50
        assert call_kwargs.get("autostart") is False

    def test_update_player_rename_collision(self, client, mock_manager):
        """Test updating player to name that already exists."""
        mock_manager.update_player.return_value = (False, "Player name already exists")

        response = client.put(
            "/api/players/Kitchen",
            json={"name": "Bedroom"},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False

    def test_update_player_no_update_method(self, client, mock_manager):
        """Test update player when manager lacks update_player method."""
        del mock_manager.update_player

        response = client.put(
            "/api/players/Kitchen",
            json={"name": "Kitchen2"},
        )

        assert response.status_code == 500
        data = response.get_json()
        assert data["success"] is False
        assert "does not support player updates" in data["error"].lower()


# =============================================================================
# TESTS - DELETE /api/players/<name> (Delete Player)
# =============================================================================


class TestDeletePlayer:
    """Tests for DELETE /api/players/<name> endpoint."""

    def test_delete_player_success(self, client, mock_manager):
        """Test deleting an existing player successfully."""
        mock_manager.delete_player.return_value = (True, "Player deleted successfully")

        response = client.delete("/api/players/Kitchen")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["message"] == "Player deleted successfully"
        mock_manager.delete_player.assert_called_once_with("Kitchen")

    def test_delete_player_not_found(self, client, mock_manager):
        """Test deleting a non-existent player."""
        mock_manager.delete_player.return_value = (False, "Player not found")

        response = client.delete("/api/players/NonExistent")

        assert response.status_code == 404  # API returns 404 on delete failure
        data = response.get_json()
        assert data["success"] is False
        assert "not found" in data["error"].lower()

    def test_delete_player_url_encoded_name(self, client, mock_manager):
        """Test deleting a player with URL-encoded special characters in name."""
        mock_manager.delete_player.return_value = (True, "Deleted")

        response = client.delete("/api/players/Living%20Room")

        assert response.status_code == 200
        mock_manager.delete_player.assert_called_once_with("Living Room")

    def test_delete_player_manager_error(self, client, mock_manager):
        """Test delete when manager returns an error."""
        mock_manager.delete_player.return_value = (False, "Cannot delete running player")

        response = client.delete("/api/players/Kitchen")

        assert response.status_code == 404  # API returns 404 on delete failure
        data = response.get_json()
        assert data["success"] is False
        assert "cannot delete" in data["error"].lower()


# =============================================================================
# TESTS - POST /api/players/<name>/start (Start Player)
# =============================================================================


class TestStartPlayer:
    """Tests for POST /api/players/<name>/start endpoint."""

    def test_start_player_success(self, client, mock_manager):
        """Test starting a player successfully."""
        mock_manager.start_player.return_value = (True, "Player started successfully")

        response = client.post("/api/players/Kitchen/start")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["message"] == "Player started successfully"
        mock_manager.start_player.assert_called_once_with("Kitchen")

    def test_start_player_not_found(self, client, mock_manager):
        """Test starting a non-existent player."""
        mock_manager.start_player.return_value = (False, "Player not found")

        response = client.post("/api/players/NonExistent/start")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is False
        assert "not found" in data["message"].lower()

    def test_start_player_already_running(self, client, mock_manager):
        """Test starting a player that's already running."""
        mock_manager.start_player.return_value = (False, "Player is already running")

        response = client.post("/api/players/Kitchen/start")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is False
        assert "already running" in data["message"].lower()

    def test_start_player_device_error(self, client, mock_manager):
        """Test starting a player when device is unavailable."""
        mock_manager.start_player.return_value = (False, "Device not found: hw:0,0")

        response = client.post("/api/players/Kitchen/start")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is False
        assert "device not found" in data["message"].lower()


# =============================================================================
# TESTS - POST /api/players/<name>/stop (Stop Player)
# =============================================================================


class TestStopPlayer:
    """Tests for POST /api/players/<name>/stop endpoint."""

    def test_stop_player_success(self, client, mock_manager):
        """Test stopping a player successfully."""
        mock_manager.stop_player.return_value = (True, "Player stopped successfully")

        response = client.post("/api/players/Kitchen/stop")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["message"] == "Player stopped successfully"
        mock_manager.stop_player.assert_called_once_with("Kitchen")

    def test_stop_player_not_found(self, client, mock_manager):
        """Test stopping a non-existent player."""
        mock_manager.stop_player.return_value = (False, "Player not found")

        response = client.post("/api/players/NonExistent/stop")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is False
        assert "not found" in data["message"].lower()

    def test_stop_player_not_running(self, client, mock_manager):
        """Test stopping a player that's not running."""
        mock_manager.stop_player.return_value = (False, "Player is not running")

        response = client.post("/api/players/Kitchen/stop")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is False
        assert "not running" in data["message"].lower()


# =============================================================================
# TESTS - GET /api/players/<name>/volume (Get Volume)
# =============================================================================


class TestGetVolume:
    """Tests for GET /api/players/<name>/volume endpoint."""

    def test_get_volume_success(self, client, mock_manager):
        """Test getting player volume successfully."""
        mock_manager.get_player_volume.return_value = 75

        response = client.get("/api/players/Kitchen/volume")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["volume"] == 75
        mock_manager.get_player_volume.assert_called_once_with("Kitchen")

    def test_get_volume_player_not_found(self, client, mock_manager):
        """Test getting volume for non-existent player."""
        mock_manager.get_player_volume.return_value = None

        response = client.get("/api/players/NonExistent/volume")

        assert response.status_code == 404
        data = response.get_json()
        assert data["success"] is False
        assert "not found" in data["message"].lower()

    def test_get_volume_zero(self, client, mock_manager):
        """Test getting volume when player is muted."""
        mock_manager.get_player_volume.return_value = 0

        response = client.get("/api/players/Kitchen/volume")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["volume"] == 0

    def test_get_volume_max(self, client, mock_manager):
        """Test getting maximum volume."""
        mock_manager.get_player_volume.return_value = 100

        response = client.get("/api/players/Kitchen/volume")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["volume"] == 100

    def test_get_volume_manager_exception(self, client, mock_manager):
        """Test get volume when manager raises exception."""
        mock_manager.get_player_volume.side_effect = RuntimeError("Hardware error")

        response = client.get("/api/players/Kitchen/volume")

        assert response.status_code == 500
        data = response.get_json()
        assert data["success"] is False
        assert "server error" in data["message"].lower()


# =============================================================================
# TESTS - POST /api/players/<name>/volume (Set Volume)
# =============================================================================


class TestSetVolume:
    """Tests for POST /api/players/<name>/volume endpoint."""

    def test_set_volume_success(self, client, mock_manager):
        """Test setting player volume successfully."""
        mock_manager.set_player_volume.return_value = (True, "Volume set to 75")

        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": 75},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        mock_manager.set_player_volume.assert_called_once_with("Kitchen", 75)

    def test_set_volume_zero(self, client, mock_manager):
        """Test setting volume to zero (mute)."""
        mock_manager.set_player_volume.return_value = (True, "Volume set to 0")

        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": 0},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        mock_manager.set_player_volume.assert_called_once_with("Kitchen", 0)

    def test_set_volume_max(self, client, mock_manager):
        """Test setting maximum volume."""
        mock_manager.set_player_volume.return_value = (True, "Volume set to 100")

        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": 100},
        )

        assert response.status_code == 200
        mock_manager.set_player_volume.assert_called_once_with("Kitchen", 100)

    def test_set_volume_missing(self, client, mock_manager):
        """Test setting volume without providing volume value."""
        response = client.post(
            "/api/players/Kitchen/volume",
            json={},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "volume is required" in data["message"].lower()
        mock_manager.set_player_volume.assert_not_called()

    def test_set_volume_null(self, client, mock_manager):
        """Test setting volume to null."""
        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": None},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "volume is required" in data["message"].lower()

    def test_set_volume_invalid_string(self, client, mock_manager):
        """Test setting volume to non-numeric string."""
        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": "loud"},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "must be a number" in data["message"].lower()

    def test_set_volume_numeric_string(self, client, mock_manager):
        """Test setting volume with numeric string (should be converted)."""
        mock_manager.set_player_volume.return_value = (True, "Volume set")

        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": "75"},
        )

        assert response.status_code == 200
        mock_manager.set_player_volume.assert_called_once_with("Kitchen", 75)

    def test_set_volume_out_of_range_high(self, client, mock_manager):
        """Test setting volume above 100 (manager handles validation)."""
        mock_manager.set_player_volume.return_value = (False, "Volume must be between 0 and 100")

        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": 150},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is False
        mock_manager.set_player_volume.assert_called_once_with("Kitchen", 150)

    def test_set_volume_negative(self, client, mock_manager):
        """Test setting negative volume (manager handles validation)."""
        mock_manager.set_player_volume.return_value = (False, "Volume must be between 0 and 100")

        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": -10},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is False

    def test_set_volume_player_not_found(self, client, mock_manager):
        """Test setting volume for non-existent player."""
        mock_manager.set_player_volume.return_value = (False, "Player not found")

        response = client.post(
            "/api/players/NonExistent/volume",
            json={"volume": 50},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is False
        assert "not found" in data["message"].lower()

    def test_set_volume_manager_exception(self, client, mock_manager):
        """Test set volume when manager raises exception."""
        mock_manager.set_player_volume.side_effect = RuntimeError("Hardware error")

        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": 50},
        )

        assert response.status_code == 500
        data = response.get_json()
        assert data["success"] is False
        assert "server error" in data["message"].lower()

    def test_set_volume_float_truncated(self, client, mock_manager):
        """Test setting volume with float (should be truncated to int)."""
        mock_manager.set_player_volume.return_value = (True, "Volume set")

        response = client.post(
            "/api/players/Kitchen/volume",
            json={"volume": 75.9},
        )

        assert response.status_code == 200
        # Float should be converted to int
        mock_manager.set_player_volume.assert_called_once_with("Kitchen", 75)


# =============================================================================
# TESTS - GET /api/players/<name>/status (Get Status)
# =============================================================================


class TestGetPlayerStatus:
    """Tests for GET /api/players/<name>/status endpoint."""

    def test_get_status_running(self, client, mock_manager):
        """Test getting status of running player."""
        mock_manager.get_player_status.return_value = True

        response = client.get("/api/players/Kitchen/status")

        assert response.status_code == 200
        data = response.get_json()
        assert data["running"] is True
        mock_manager.get_player_status.assert_called_once_with("Kitchen")

    def test_get_status_stopped(self, client, mock_manager):
        """Test getting status of stopped player."""
        mock_manager.get_player_status.return_value = False

        response = client.get("/api/players/Kitchen/status")

        assert response.status_code == 200
        data = response.get_json()
        assert data["running"] is False


# =============================================================================
# TESTS - Edge Cases and Error Handling
# =============================================================================


class TestEdgeCases:
    """Tests for edge cases and error handling."""

    def test_player_name_with_special_characters(self, client, mock_manager):
        """Test handling player names with special characters."""
        mock_manager.delete_player.return_value = (True, "Deleted")

        # URL-encoded special characters
        response = client.delete("/api/players/Player%201%20%26%202")

        assert response.status_code == 200
        mock_manager.delete_player.assert_called_once_with("Player 1 & 2")

    def test_player_name_with_unicode(self, client, mock_manager):
        """Test handling player names with unicode characters."""
        mock_manager.start_player.return_value = (True, "Started")

        response = client.post("/api/players/%E5%AE%A2%E5%8E%85/start")  # Chinese characters

        assert response.status_code == 200

    def test_empty_json_body(self, client, mock_manager):
        """Test endpoints handle empty JSON body."""
        response = client.post(
            "/api/players/Kitchen/volume",
            json={},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False

    def test_malformed_json_body(self, client, mock_manager):
        """Test endpoints handle malformed JSON."""
        response = client.post(
            "/api/players",
            data="not valid json",
            content_type="application/json",
        )

        # Flask returns 400 for malformed JSON
        assert response.status_code == 400

    def test_create_player_null_body(self, client, mock_manager):
        """Test create player with null request body."""
        response = client.post(
            "/api/players",
            data="null",
            content_type="application/json",
        )

        assert response.status_code == 400


# =============================================================================
# TESTS - GET /api/players (List All Players)
# =============================================================================


class TestGetPlayers:
    """Tests for GET /api/players endpoint."""

    def test_get_players_empty(self, client, mock_manager):
        """Test getting players when none exist."""
        mock_manager.players = {}
        mock_manager.get_all_statuses.return_value = {}

        response = client.get("/api/players")

        assert response.status_code == 200
        data = response.get_json()
        assert data["players"] == {}
        assert data["statuses"] == {}

    def test_get_players_with_players(self, client, mock_manager):
        """Test getting players when several exist."""
        mock_manager.players = {
            "Kitchen": {"name": "Kitchen", "device": "hw:0,0"},
            "Bedroom": {"name": "Bedroom", "device": "hw:1,0"},
        }
        mock_manager.get_all_statuses.return_value = {
            "Kitchen": True,
            "Bedroom": False,
        }

        response = client.get("/api/players")

        assert response.status_code == 200
        data = response.get_json()
        assert "Kitchen" in data["players"]
        assert "Bedroom" in data["players"]
        assert data["statuses"]["Kitchen"] is True
        assert data["statuses"]["Bedroom"] is False


# =============================================================================
# TESTS - GET /api/players/<name> (Get Single Player)
# =============================================================================


class TestGetPlayer:
    """Tests for GET /api/players/<name> endpoint."""

    def test_get_player_exists(self, client, mock_manager):
        """Test getting an existing player's configuration."""
        mock_manager.config = Mock()
        mock_manager.config.get_player.return_value = {
            "name": "Kitchen",
            "device": "hw:0,0",
            "provider": "squeezelite",
            "server_ip": "192.168.1.100",
        }

        response = client.get("/api/players/Kitchen")

        assert response.status_code == 200
        data = response.get_json()
        assert data["name"] == "Kitchen"
        assert data["device"] == "hw:0,0"
        mock_manager.config.get_player.assert_called_once_with("Kitchen")

    def test_get_player_not_found(self, client, mock_manager):
        """Test getting a non-existent player."""
        mock_manager.config = Mock()
        mock_manager.config.get_player.return_value = None

        response = client.get("/api/players/NonExistent")

        assert response.status_code == 404
        data = response.get_json()
        assert data["success"] is False
        assert "not found" in data["error"].lower()


# =============================================================================
# TESTS - GET /api/devices (ALSA Devices)
# =============================================================================


class TestGetDevices:
    """Tests for GET /api/devices endpoint."""

    def test_get_devices_success(self, client, mock_manager):
        """Test getting ALSA audio devices."""
        mock_manager.get_audio_devices.return_value = [
            {"id": "hw:0,0", "name": "Built-in Audio"},
            {"id": "hw:1,0", "name": "USB DAC"},
        ]

        response = client.get("/api/devices")

        assert response.status_code == 200
        data = response.get_json()
        assert len(data["devices"]) == 2
        assert data["devices"][0]["id"] == "hw:0,0"
        mock_manager.get_audio_devices.assert_called_once()

    def test_get_devices_empty(self, client, mock_manager):
        """Test getting devices when none available."""
        mock_manager.get_audio_devices.return_value = []

        response = client.get("/api/devices")

        assert response.status_code == 200
        data = response.get_json()
        assert data["devices"] == []

    def test_get_devices_error(self, client, mock_manager):
        """Test error handling when device detection fails."""
        mock_manager.get_audio_devices.side_effect = RuntimeError("ALSA error")

        response = client.get("/api/devices")

        assert response.status_code == 500
        data = response.get_json()
        assert data["devices"] == []
        assert "error" in data


# =============================================================================
# TESTS - GET /api/providers
# =============================================================================


class TestGetProviders:
    """Tests for GET /api/providers endpoint."""

    def test_get_providers_player_manager(self, client, mock_manager):
        """Test getting providers with PlayerManager."""
        mock_manager.get_available_providers.return_value = [
            {"type": "squeezelite", "name": "Squeezelite", "available": True},
            {"type": "sendspin", "name": "Sendspin", "available": True},
        ]

        response = client.get("/api/providers")

        assert response.status_code == 200
        data = response.get_json()
        assert len(data["providers"]) == 2
        assert data["providers"][0]["type"] == "squeezelite"

    def test_get_providers_squeezelite_manager(self, client_squeezelite, mock_squeezelite_manager):
        """Test getting providers with SqueezeliteManager (default provider only)."""
        response = client_squeezelite.get("/api/providers")

        assert response.status_code == 200
        data = response.get_json()
        assert len(data["providers"]) == 1
        assert data["providers"][0]["type"] == "squeezelite"

    def test_get_providers_fallback_to_all(self, client, mock_manager):
        """Test provider listing falls back to all when none available."""
        mock_manager.get_available_providers.return_value = []
        mock_manager.providers = Mock()
        mock_manager.providers.get_provider_info.return_value = [
            {"type": "squeezelite", "name": "Squeezelite", "available": False},
        ]

        response = client.get("/api/providers")

        assert response.status_code == 200
        data = response.get_json()
        assert len(data["providers"]) == 1

    def test_get_providers_error(self, client, mock_manager):
        """Test error handling when provider lookup fails."""
        mock_manager.get_available_providers.side_effect = RuntimeError("Provider error")

        response = client.get("/api/providers")

        assert response.status_code == 500
        data = response.get_json()
        assert data["success"] is False


# =============================================================================
# TESTS - PUT /api/players/<n>/offset
# =============================================================================


class TestUpdatePlayerOffset:
    """Tests for PUT /api/players/<n>/offset endpoint."""

    def test_update_offset_success(self, client, mock_manager):
        """Test updating player sync offset successfully."""
        mock_manager.config = Mock()
        mock_manager.config.update_player_field.return_value = True

        response = client.put(
            "/api/players/Kitchen/offset",
            json={"delay_ms": -150},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        assert data["delay_ms"] == -150
        assert data["restart_required"] is True
        mock_manager.config.update_player_field.assert_called_once_with("Kitchen", "delay_ms", -150)

    def test_update_offset_zero(self, client, mock_manager):
        """Test setting offset to zero."""
        mock_manager.config = Mock()
        mock_manager.config.update_player_field.return_value = True

        response = client.put(
            "/api/players/Kitchen/offset",
            json={"delay_ms": 0},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["delay_ms"] == 0

    def test_update_offset_positive(self, client, mock_manager):
        """Test setting positive offset."""
        mock_manager.config = Mock()
        mock_manager.config.update_player_field.return_value = True

        response = client.put(
            "/api/players/Kitchen/offset",
            json={"delay_ms": 100},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["delay_ms"] == 100

    def test_update_offset_player_not_found(self, client, mock_manager):
        """Test updating offset for non-existent player."""
        mock_manager.config = Mock()
        mock_manager.config.update_player_field.return_value = False

        response = client.put(
            "/api/players/NonExistent/offset",
            json={"delay_ms": -100},
        )

        assert response.status_code == 404
        data = response.get_json()
        assert data["success"] is False
        assert "not found" in data["message"].lower()

    def test_update_offset_invalid_type(self, client, mock_manager):
        """Test updating offset with invalid type."""
        response = client.put(
            "/api/players/Kitchen/offset",
            json={"delay_ms": "not a number"},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "integer" in data["message"].lower()

    def test_update_offset_out_of_range_high(self, client, mock_manager):
        """Test updating offset above maximum range."""
        response = client.put(
            "/api/players/Kitchen/offset",
            json={"delay_ms": 2000},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "-1000" in data["message"] or "1000" in data["message"]

    def test_update_offset_out_of_range_low(self, client, mock_manager):
        """Test updating offset below minimum range."""
        response = client.put(
            "/api/players/Kitchen/offset",
            json={"delay_ms": -2000},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False


# =============================================================================
# TESTS - GET /api/debug/audio
# =============================================================================


class TestDebugAudio:
    """Tests for GET /api/debug/audio endpoint."""

    def test_debug_audio_success(self, client, mock_manager):
        """Test debug audio endpoint returns device information."""
        mock_manager.get_audio_devices.return_value = [
            {"id": "hw:0,0", "name": "Built-in Audio"},
        ]
        # Remove get_mixer_controls to avoid iteration issues
        del mock_manager.get_mixer_controls

        with patch("common.subprocess.run") as mock_run:
            mock_run.return_value = Mock(stdout="card 0: Audio", returncode=0)
            response = client.get("/api/debug/audio")

        assert response.status_code == 200
        data = response.get_json()
        assert "detected_devices" in data
        assert "container_mode" in data

    def test_debug_audio_with_mixer_controls(self, client, mock_manager):
        """Test debug audio endpoint includes mixer controls."""
        mock_manager.get_audio_devices.return_value = [
            {"id": "hw:0,0", "name": "Built-in Audio"},
        ]
        mock_manager.get_mixer_controls.return_value = ["Master", "PCM"]

        with patch("common.subprocess.run") as mock_run:
            mock_run.return_value = Mock(stdout="", returncode=0)
            response = client.get("/api/debug/audio")

        assert response.status_code == 200
        data = response.get_json()
        assert "mixer_controls" in data


# =============================================================================
# TESTS - GET /api/state and POST /api/state/save
# =============================================================================


class TestStateEndpoints:
    """Tests for state management endpoints."""

    def test_get_state_supported(self, client, mock_manager):
        """Test getting state when manager supports it."""
        mock_manager.get_state_info.return_value = {
            "running_players": ["Kitchen"],
            "last_saved": "2024-01-01T12:00:00",
        }

        response = client.get("/api/state")

        assert response.status_code == 200
        data = response.get_json()
        assert "running_players" in data
        mock_manager.get_state_info.assert_called_once()

    def test_get_state_not_supported(self, client_squeezelite, mock_squeezelite_manager):
        """Test getting state when manager doesn't support it."""
        response = client_squeezelite.get("/api/state")

        assert response.status_code == 404
        data = response.get_json()
        assert "not available" in data["message"].lower()

    def test_save_state_success(self, client, mock_manager):
        """Test saving state successfully."""
        mock_manager.save_state.return_value = None

        response = client.post("/api/state/save")

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
        mock_manager.save_state.assert_called_once()

    def test_save_state_error(self, client, mock_manager):
        """Test saving state when error occurs."""
        mock_manager.save_state.side_effect = IOError("Disk full")

        response = client.post("/api/state/save")

        assert response.status_code == 500
        data = response.get_json()
        assert data["success"] is False

    def test_save_state_not_supported(self, client_squeezelite, mock_squeezelite_manager):
        """Test saving state when manager doesn't support it."""
        response = client_squeezelite.post("/api/state/save")

        assert response.status_code == 404
        data = response.get_json()
        assert data["success"] is False


# =============================================================================
# TESTS - Player Name Validation
# =============================================================================


class TestPlayerNameValidation:
    """Tests for player name validation in create and update endpoints."""

    def test_create_player_name_too_long(self, client, mock_manager):
        """Test creating a player with name exceeding max length."""
        long_name = "A" * 65  # MAX_NAME_LENGTH is 64

        response = client.post(
            "/api/players",
            json={"name": long_name, "device": "hw:0,0"},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "64 characters" in data["error"] or "must be at most" in data["error"]
        mock_manager.create_player.assert_not_called()

    def test_create_player_invalid_characters(self, client, mock_manager):
        """Test creating a player with invalid characters in name."""
        response = client.post(
            "/api/players",
            json={"name": "Kitchen/Dining", "device": "hw:0,0"},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "invalid characters" in data["error"].lower()

    def test_update_player_name_too_long(self, client, mock_manager):
        """Test updating a player with new name exceeding max length."""
        long_name = "B" * 65

        response = client.put(
            "/api/players/Kitchen",
            json={"name": long_name},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        mock_manager.update_player.assert_not_called()

    def test_update_player_invalid_characters(self, client, mock_manager):
        """Test updating a player with invalid characters in new name."""
        response = client.put(
            "/api/players/Kitchen",
            json={"name": "New\\Name"},
        )

        assert response.status_code == 400
        data = response.get_json()
        assert data["success"] is False
        assert "invalid characters" in data["error"].lower()

    def test_create_player_valid_name_with_spaces(self, client, mock_manager):
        """Test creating a player with spaces in name (valid)."""
        mock_manager.create_player.return_value = (True, "Created")

        response = client.post(
            "/api/players",
            json={"name": "Living Room", "device": "hw:0,0"},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True

    def test_create_player_valid_name_with_numbers(self, client, mock_manager):
        """Test creating a player with numbers in name (valid)."""
        mock_manager.create_player.return_value = (True, "Created")

        response = client.post(
            "/api/players",
            json={"name": "Room 101", "device": "hw:0,0"},
        )

        assert response.status_code == 200
        data = response.get_json()
        assert data["success"] is True
