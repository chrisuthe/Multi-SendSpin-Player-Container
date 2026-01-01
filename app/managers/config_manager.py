"""
Configuration Manager for player persistence.

Handles loading, saving, and validating player configurations
stored in YAML format. Integrates Pydantic schema validation
for type safety and format correctness.
"""

import logging
import os
from typing import Any

import yaml
from schemas.player_config import (
    validate_player_config,
    validate_players_file,
)

logger = logging.getLogger(__name__)

# Type alias for player configuration
PlayerConfig = dict[str, Any]


class ConfigValidationError(Exception):
    """
    Exception raised when player configuration validation fails.

    Attributes:
        message: Human-readable error summary.
        errors: List of individual validation error messages.
        player_name: Name of the player that failed validation (if applicable).
    """

    def __init__(
        self,
        message: str,
        errors: list[str] | None = None,
        player_name: str | None = None,
    ):
        """
        Initialize validation error.

        Args:
            message: Human-readable error summary.
            errors: List of individual validation error messages.
            player_name: Name of the player that failed validation.
        """
        super().__init__(message)
        self.message = message
        self.errors = errors or []
        self.player_name = player_name

    def __str__(self) -> str:
        """Return formatted error message."""
        if not self.errors:
            return self.message
        return f"{self.message}: {'; '.join(self.errors)}"


class ConfigManager:
    """
    Manages player configuration persistence with schema validation.

    Handles loading and saving player configurations to/from YAML files.
    Provides a clean interface for configuration operations without
    coupling to specific player implementations.

    Integrates Pydantic schema validation to ensure configurations
    meet type and format requirements before saving.

    Attributes:
        config_path: Path to the YAML configuration file.
        players: Dictionary mapping player names to their configurations.
        validate_on_load: Whether to validate configurations when loading.
        validate_on_save: Whether to validate configurations before saving.
    """

    def __init__(
        self,
        config_path: str,
        validate_on_load: bool = True,
        validate_on_save: bool = True,
    ) -> None:
        """
        Initialize the ConfigManager.

        Args:
            config_path: Path to the YAML configuration file.
            validate_on_load: If True, validate configurations when loading
                from file. Invalid configs are logged but not loaded.
            validate_on_save: If True, validate configurations before saving
                to file. Invalid configs raise ConfigValidationError.
        """
        self.config_path = config_path
        self.validate_on_load = validate_on_load
        self.validate_on_save = validate_on_save
        self.players: dict[str, PlayerConfig] = {}

        # Ensure config directory exists
        config_dir = os.path.dirname(config_path)
        if config_dir:
            os.makedirs(config_dir, exist_ok=True)

        self.load()

    def load(self) -> dict[str, PlayerConfig]:
        """
        Load player configuration from the YAML configuration file.

        Reads the config file and populates self.players with stored
        player configurations. If the file doesn't exist or contains
        invalid YAML, initializes with an empty dictionary.

        When validate_on_load is True, each player configuration is
        validated against the schema. Invalid configurations are logged
        as warnings but skipped (not loaded), allowing valid configs
        to still be used.

        Returns:
            Dictionary of player configurations (only valid ones if
            validation is enabled).

        Side Effects:
            - Modifies self.players dictionary
            - Logs errors if file reading or YAML parsing fails
            - Logs warnings for invalid player configurations
        """
        if os.path.exists(self.config_path):
            try:
                with open(self.config_path) as f:
                    raw_players = yaml.safe_load(f) or {}

                if self.validate_on_load and raw_players:
                    is_valid, errors, validated_players = validate_players_file(raw_players)
                    if not is_valid:
                        for error in errors:
                            logger.warning(f"Skipping invalid config: {error}")
                    self.players = validated_players
                    logger.debug(
                        f"Loaded {len(self.players)} valid players from "
                        f"{self.config_path} ({len(raw_players) - len(self.players)} "
                        f"invalid skipped)"
                    )
                else:
                    self.players = raw_players
                    logger.debug(f"Loaded {len(self.players)} players from {self.config_path}")

            except Exception as e:
                logger.error(f"Error loading config from {self.config_path}: {e}")
                self.players = {}
        else:
            logger.info(f"Config file {self.config_path} does not exist, starting fresh")
            self.players = {}

        return self.players

    def save(self) -> bool:
        """
        Save current player configuration to the YAML configuration file.

        Writes self.players dictionary to the config file in YAML format.
        Uses block style (not flow style) for human readability.

        When validate_on_save is True, all configurations are validated
        before saving. If any configuration is invalid, the save is
        aborted and a ConfigValidationError is raised.

        Returns:
            True if save was successful, False otherwise.

        Raises:
            ConfigValidationError: If validate_on_save is True and any
                player configuration is invalid.

        Side Effects:
            - Writes to config_path
            - Logs errors if file writing fails
        """
        # Validate before saving if enabled
        if self.validate_on_save and self.players:
            is_valid, errors, _ = validate_players_file(self.players)
            if not is_valid:
                raise ConfigValidationError(
                    "Cannot save invalid configuration",
                    errors=errors,
                )

        try:
            with open(self.config_path, "w") as f:
                yaml.dump(self.players, f, default_flow_style=False)
            logger.debug(f"Saved {len(self.players)} players to {self.config_path}")
            return True
        except ConfigValidationError:
            # Re-raise validation errors
            raise
        except Exception as e:
            logger.error(f"Error saving config to {self.config_path}: {e}")
            return False

    def get_player(self, name: str) -> PlayerConfig | None:
        """
        Get a player configuration by name.

        Args:
            name: Name of the player.

        Returns:
            Player configuration dictionary, or None if not found.
        """
        return self.players.get(name)

    def set_player(
        self,
        name: str,
        config: PlayerConfig,
        validate: bool | None = None,
    ) -> None:
        """
        Set a player configuration.

        Args:
            name: Name of the player.
            config: Player configuration dictionary.
            validate: Whether to validate the config before setting.
                If None, uses self.validate_on_save setting.

        Raises:
            ConfigValidationError: If validation is enabled and config
                is invalid.

        Side Effects:
            - Modifies self.players dictionary
            - Does NOT automatically save (call save() explicitly)
        """
        should_validate = validate if validate is not None else self.validate_on_save

        if should_validate:
            is_valid, error_msg, validated_config = validate_player_config(config, name)
            if not is_valid:
                raise ConfigValidationError(
                    f"Invalid configuration for player '{name}'",
                    errors=[error_msg],
                    player_name=name,
                )
            # Use the validated (normalized) config
            if validated_config:
                config = validated_config

        self.players[name] = config

    def update_player_field(
        self,
        name: str,
        field: str,
        value: Any,
        save: bool = True,
    ) -> bool:
        """
        Update a single field in a player's configuration.

        Useful for updating individual settings (like delay_ms) without
        replacing the entire configuration.

        Args:
            name: Name of the player to update.
            field: Configuration field name to update.
            value: New value for the field.
            save: Whether to save to disk after updating (default True).

        Returns:
            True if update was successful, False if player not found.

        Side Effects:
            - Modifies self.players dictionary
            - Saves to disk if save=True
        """
        if name not in self.players:
            return False

        self.players[name][field] = value
        logger.debug(f"Updated {name}.{field} = {value}")

        if save:
            self.save()

        return True

    def delete_player(self, name: str) -> bool:
        """
        Delete a player configuration.

        Args:
            name: Name of the player to delete.

        Returns:
            True if player was deleted, False if not found.

        Side Effects:
            - Modifies self.players dictionary
            - Does NOT automatically save (call save() explicitly)
        """
        if name in self.players:
            del self.players[name]
            return True
        return False

    def rename_player(self, old_name: str, new_name: str) -> bool:
        """
        Rename a player (change its key in the config).

        Args:
            old_name: Current name of the player.
            new_name: New name for the player.

        Returns:
            True if rename was successful, False if old_name not found
            or new_name already exists.

        Side Effects:
            - Modifies self.players dictionary
            - Does NOT automatically save (call save() explicitly)
        """
        if old_name not in self.players:
            return False
        if new_name in self.players and old_name != new_name:
            return False

        config = self.players[old_name]
        config["name"] = new_name
        del self.players[old_name]
        self.players[new_name] = config
        return True

    def player_exists(self, name: str) -> bool:
        """
        Check if a player exists.

        Args:
            name: Name of the player.

        Returns:
            True if player exists, False otherwise.
        """
        return name in self.players

    def list_players(self) -> list[str]:
        """
        Get list of all player names.

        Returns:
            List of player names.
        """
        return list(self.players.keys())

    def validate_config(
        self,
        config: PlayerConfig,
        name: str | None = None,
    ) -> tuple[bool, str, PlayerConfig | None]:
        """
        Validate a player configuration without storing it.

        Useful for checking user input before committing changes.

        Args:
            config: Player configuration dictionary to validate.
            name: Optional player name (used if not in config).

        Returns:
            Tuple of (is_valid, error_message, validated_config).
            If valid, error_message is empty and validated_config
            contains the normalized configuration with defaults.
            If invalid, validated_config is None.

        Example:
            >>> is_valid, error, config = manager.validate_config({
            ...     "name": "Kitchen",
            ...     "device": "hw:1,0",
            ...     "provider": "squeezelite"
            ... })
            >>> if is_valid:
            ...     manager.set_player("Kitchen", config, validate=False)
            ...     manager.save()
        """
        return validate_player_config(config, name)

    def validate_all(self) -> tuple[bool, list[str]]:
        """
        Validate all current player configurations.

        Checks all players in self.players against the schema.

        Returns:
            Tuple of (all_valid, error_messages).
            If all valid, error_messages is empty.
        """
        if not self.players:
            return True, []

        is_valid, errors, _ = validate_players_file(self.players)
        return is_valid, errors
