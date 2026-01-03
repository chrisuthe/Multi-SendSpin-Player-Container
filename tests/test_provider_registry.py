"""
Tests for the ProviderRegistry class.

Tests cover registry initialization, provider registration, provider lookup,
listing providers, validation delegation, and configuration preparation.
"""

from unittest.mock import Mock, patch

import pytest
from providers.base import PlayerProvider
from providers.registry import DEFAULT_PROVIDER, ProviderRegistry


# =============================================================================
# FIXTURES
# =============================================================================


@pytest.fixture
def registry():
    """Create an empty ProviderRegistry instance."""
    return ProviderRegistry()


@pytest.fixture
def mock_provider():
    """Create a mock provider instance.

    Note: We don't use spec=PlayerProvider because prepare_config is not
    part of the base class but is checked via hasattr() in the registry.
    """
    provider = Mock()
    provider.provider_type = "mock"
    provider.display_name = "Mock Provider"
    provider.binary_name = "mock-binary"
    provider.is_available.return_value = True
    provider.validate_config.return_value = (True, "")
    provider.get_default_config.return_value = {"provider": "mock", "volume": 75}
    provider.prepare_config.return_value = {"name": "Test", "provider": "mock", "volume": 75}
    return provider


@pytest.fixture
def mock_unavailable_provider():
    """Create a mock provider that is not available."""
    provider = Mock(spec=PlayerProvider)
    provider.provider_type = "unavailable"
    provider.display_name = "Unavailable Provider"
    provider.binary_name = "unavailable-binary"
    provider.is_available.return_value = False
    provider.validate_config.return_value = (True, "")
    provider.get_default_config.return_value = {"provider": "unavailable"}
    return provider


@pytest.fixture
def mock_provider_class():
    """Create a mock provider class."""
    return Mock(spec=type(PlayerProvider))


@pytest.fixture
def populated_registry(registry, mock_provider, mock_unavailable_provider):
    """Create a registry with registered providers."""
    registry.register_instance("mock", mock_provider)
    registry.register_instance("unavailable", mock_unavailable_provider)
    return registry


# =============================================================================
# TEST INITIALIZATION
# =============================================================================


class TestProviderRegistryInit:
    """Tests for ProviderRegistry initialization."""

    def test_init_creates_empty_providers_dict(self, registry):
        """Test that registry initializes with empty providers dictionary."""
        assert registry._providers == {}

    def test_init_creates_empty_provider_classes_dict(self, registry):
        """Test that registry initializes with empty provider classes dictionary."""
        assert registry._provider_classes == {}

    def test_init_sets_default_provider(self, registry):
        """Test that registry sets default provider type."""
        assert registry.default_provider == DEFAULT_PROVIDER

    def test_default_provider_is_squeezelite(self, registry):
        """Test that the default provider is squeezelite."""
        assert registry.default_provider == "squeezelite"


# =============================================================================
# TEST REGISTER_CLASS
# =============================================================================


class TestRegisterClass:
    """Tests for register_class method."""

    def test_register_class_adds_to_provider_classes(self, registry, mock_provider_class):
        """Test that register_class adds class to _provider_classes."""
        registry.register_class("testprovider", mock_provider_class)
        assert "testprovider" in registry._provider_classes
        assert registry._provider_classes["testprovider"] == mock_provider_class

    def test_register_class_overwrites_existing(self, registry, mock_provider_class):
        """Test that register_class overwrites existing class registration."""
        first_class = Mock()
        registry.register_class("testprovider", first_class)
        registry.register_class("testprovider", mock_provider_class)
        assert registry._provider_classes["testprovider"] == mock_provider_class

    def test_register_class_does_not_affect_instances(self, registry, mock_provider_class, mock_provider):
        """Test that register_class does not affect _providers dictionary."""
        registry.register_instance("testprovider", mock_provider)
        registry.register_class("testprovider", mock_provider_class)
        assert registry._providers["testprovider"] == mock_provider


# =============================================================================
# TEST REGISTER_INSTANCE
# =============================================================================


class TestRegisterInstance:
    """Tests for register_instance method."""

    def test_register_instance_adds_provider(self, registry, mock_provider):
        """Test that register_instance adds provider to registry."""
        registry.register_instance("mock", mock_provider)
        assert "mock" in registry._providers
        assert registry._providers["mock"] == mock_provider

    def test_register_instance_overwrites_existing(self, registry, mock_provider):
        """Test that register_instance overwrites existing provider."""
        first_provider = Mock(spec=PlayerProvider)
        registry.register_instance("mock", first_provider)
        registry.register_instance("mock", mock_provider)
        assert registry._providers["mock"] == mock_provider

    def test_register_instance_multiple_providers(self, registry, mock_provider, mock_unavailable_provider):
        """Test registering multiple providers."""
        registry.register_instance("mock", mock_provider)
        registry.register_instance("unavailable", mock_unavailable_provider)
        assert len(registry._providers) == 2
        assert "mock" in registry._providers
        assert "unavailable" in registry._providers


# =============================================================================
# TEST GET
# =============================================================================


class TestGet:
    """Tests for get method."""

    def test_get_existing_provider(self, populated_registry, mock_provider):
        """Test getting an existing provider."""
        result = populated_registry.get("mock")
        assert result == mock_provider

    def test_get_nonexistent_provider(self, populated_registry):
        """Test getting a non-existent provider returns None."""
        result = populated_registry.get("nonexistent")
        assert result is None

    def test_get_empty_registry(self, registry):
        """Test getting from empty registry returns None."""
        result = registry.get("any")
        assert result is None

    def test_get_case_sensitive(self, registry, mock_provider):
        """Test that provider lookup is case-sensitive."""
        registry.register_instance("Mock", mock_provider)
        assert registry.get("Mock") == mock_provider
        assert registry.get("mock") is None


# =============================================================================
# TEST GET_OR_DEFAULT
# =============================================================================


class TestGetOrDefault:
    """Tests for get_or_default method."""

    def test_get_or_default_with_type(self, populated_registry, mock_provider):
        """Test get_or_default with specified provider type."""
        result = populated_registry.get_or_default("mock")
        assert result == mock_provider

    def test_get_or_default_with_none(self, registry, mock_provider):
        """Test get_or_default with None uses default provider."""
        registry.register_instance("squeezelite", mock_provider)
        result = registry.get_or_default(None)
        assert result == mock_provider

    def test_get_or_default_nonexistent_returns_none(self, populated_registry):
        """Test get_or_default returns None for nonexistent provider."""
        result = populated_registry.get_or_default("nonexistent")
        assert result is None

    def test_get_or_default_with_none_and_no_default(self, registry, mock_provider):
        """Test get_or_default with None when default is not registered."""
        registry.register_instance("mock", mock_provider)
        result = registry.get_or_default(None)
        assert result is None


# =============================================================================
# TEST GET_FOR_PLAYER
# =============================================================================


class TestGetForPlayer:
    """Tests for get_for_player method."""

    def test_get_for_player_with_provider_key(self, populated_registry, mock_provider):
        """Test get_for_player uses provider key from config."""
        config = {"name": "Test", "provider": "mock"}
        result = populated_registry.get_for_player(config)
        assert result == mock_provider

    def test_get_for_player_without_provider_key(self, registry, mock_provider):
        """Test get_for_player uses default provider when key missing."""
        registry.register_instance("squeezelite", mock_provider)
        config = {"name": "Test"}
        result = registry.get_for_player(config)
        assert result == mock_provider

    def test_get_for_player_unknown_provider(self, populated_registry):
        """Test get_for_player returns None for unknown provider."""
        config = {"name": "Test", "provider": "unknown"}
        result = populated_registry.get_for_player(config)
        assert result is None

    def test_get_for_player_empty_config(self, registry, mock_provider):
        """Test get_for_player with empty config uses default."""
        registry.register_instance("squeezelite", mock_provider)
        result = registry.get_for_player({})
        assert result == mock_provider


# =============================================================================
# TEST HAS_PROVIDER
# =============================================================================


class TestHasProvider:
    """Tests for has_provider method."""

    def test_has_provider_existing(self, populated_registry):
        """Test has_provider returns True for existing provider."""
        assert populated_registry.has_provider("mock") is True

    def test_has_provider_nonexistent(self, populated_registry):
        """Test has_provider returns False for nonexistent provider."""
        assert populated_registry.has_provider("nonexistent") is False

    def test_has_provider_empty_registry(self, registry):
        """Test has_provider returns False for empty registry."""
        assert registry.has_provider("any") is False

    def test_has_provider_case_sensitive(self, registry, mock_provider):
        """Test that has_provider is case-sensitive."""
        registry.register_instance("Mock", mock_provider)
        assert registry.has_provider("Mock") is True
        assert registry.has_provider("mock") is False


# =============================================================================
# TEST LIST_PROVIDERS
# =============================================================================


class TestListProviders:
    """Tests for list_providers method."""

    def test_list_providers_all(self, populated_registry):
        """Test list_providers returns all registered providers."""
        providers = populated_registry.list_providers()
        assert len(providers) == 2
        assert "mock" in providers
        assert "unavailable" in providers

    def test_list_providers_available_only(self, populated_registry):
        """Test list_providers with available_only=True."""
        providers = populated_registry.list_providers(available_only=True)
        assert len(providers) == 1
        assert "mock" in providers
        assert "unavailable" not in providers

    def test_list_providers_empty_registry(self, registry):
        """Test list_providers returns empty list for empty registry."""
        providers = registry.list_providers()
        assert providers == []

    def test_list_providers_all_unavailable(self, registry, mock_unavailable_provider):
        """Test list_providers with all unavailable providers."""
        registry.register_instance("unavailable1", mock_unavailable_provider)
        unavailable2 = Mock(spec=PlayerProvider)
        unavailable2.is_available.return_value = False
        registry.register_instance("unavailable2", unavailable2)

        all_providers = registry.list_providers(available_only=False)
        available_providers = registry.list_providers(available_only=True)

        assert len(all_providers) == 2
        assert len(available_providers) == 0


# =============================================================================
# TEST GET_DEFAULT_AVAILABLE_PROVIDER
# =============================================================================


class TestGetDefaultAvailableProvider:
    """Tests for get_default_available_provider method."""

    def test_get_default_available_provider_default_available(self, registry, mock_provider):
        """Test returns default provider when available."""
        registry.register_instance("squeezelite", mock_provider)
        result = registry.get_default_available_provider()
        assert result == "squeezelite"

    def test_get_default_available_provider_default_unavailable(self, registry, mock_provider, mock_unavailable_provider):
        """Test falls back to first available when default unavailable."""
        mock_unavailable_provider.provider_type = "squeezelite"
        registry.register_instance("squeezelite", mock_unavailable_provider)
        registry.register_instance("mock", mock_provider)

        result = registry.get_default_available_provider()
        assert result == "mock"

    def test_get_default_available_provider_none_available(self, registry, mock_unavailable_provider):
        """Test returns None when no providers are available."""
        registry.register_instance("unavailable", mock_unavailable_provider)
        result = registry.get_default_available_provider()
        assert result is None

    def test_get_default_available_provider_empty_registry(self, registry):
        """Test returns None for empty registry."""
        result = registry.get_default_available_provider()
        assert result is None


# =============================================================================
# TEST GET_PROVIDER_INFO
# =============================================================================


class TestGetProviderInfo:
    """Tests for get_provider_info method."""

    def test_get_provider_info_available_only_default(self, populated_registry):
        """Test get_provider_info returns only available providers by default."""
        info = populated_registry.get_provider_info()
        assert len(info) == 1
        assert info[0]["type"] == "mock"

    def test_get_provider_info_all(self, populated_registry):
        """Test get_provider_info returns all providers when available_only=False."""
        info = populated_registry.get_provider_info(available_only=False)
        assert len(info) == 2

    def test_get_provider_info_contains_expected_fields(self, populated_registry, mock_provider):
        """Test get_provider_info returns dictionaries with expected fields."""
        info = populated_registry.get_provider_info()
        assert len(info) == 1
        provider_info = info[0]
        assert "type" in provider_info
        assert "display_name" in provider_info
        assert "binary" in provider_info
        assert "available" in provider_info

    def test_get_provider_info_values(self, populated_registry):
        """Test get_provider_info returns correct values."""
        info = populated_registry.get_provider_info()
        mock_info = info[0]
        assert mock_info["type"] == "mock"
        assert mock_info["display_name"] == "Mock Provider"
        assert mock_info["binary"] == "mock-binary"
        assert mock_info["available"] is True

    def test_get_provider_info_empty_registry(self, registry):
        """Test get_provider_info returns empty list for empty registry."""
        info = registry.get_provider_info()
        assert info == []


# =============================================================================
# TEST VALIDATE_PLAYER_CONFIG
# =============================================================================


class TestValidatePlayerConfig:
    """Tests for validate_player_config method."""

    def test_validate_player_config_valid(self, populated_registry, mock_provider):
        """Test validate_player_config with valid config."""
        config = {"name": "Test", "provider": "mock"}
        is_valid, error = populated_registry.validate_player_config(config)

        mock_provider.validate_config.assert_called_once_with(config)
        assert is_valid is True
        assert error == ""

    def test_validate_player_config_invalid(self, populated_registry, mock_provider):
        """Test validate_player_config with invalid config."""
        mock_provider.validate_config.return_value = (False, "Name is required")
        config = {"provider": "mock"}
        is_valid, error = populated_registry.validate_player_config(config)

        assert is_valid is False
        assert error == "Name is required"

    def test_validate_player_config_unknown_provider(self, populated_registry):
        """Test validate_player_config with unknown provider."""
        config = {"name": "Test", "provider": "unknown"}
        is_valid, error = populated_registry.validate_player_config(config)

        assert is_valid is False
        assert "unknown provider" in error.lower()

    def test_validate_player_config_uses_default_provider(self, registry, mock_provider):
        """Test validate_player_config uses default provider when not specified."""
        registry.register_instance("squeezelite", mock_provider)
        config = {"name": "Test"}
        is_valid, error = registry.validate_player_config(config)

        mock_provider.validate_config.assert_called_once_with(config)
        assert is_valid is True

    def test_validate_player_config_default_not_registered(self, populated_registry):
        """Test validate_player_config with unregistered default provider."""
        config = {"name": "Test"}
        is_valid, error = populated_registry.validate_player_config(config)

        assert is_valid is False
        assert "unknown provider" in error.lower()


# =============================================================================
# TEST PREPARE_PLAYER_CONFIG
# =============================================================================


class TestPreparePlayerConfig:
    """Tests for prepare_player_config method."""

    def test_prepare_player_config_with_prepare_method(self, populated_registry, mock_provider):
        """Test prepare_player_config uses provider's prepare_config method."""
        config = {"name": "Test", "provider": "mock"}
        result = populated_registry.prepare_player_config(config)

        mock_provider.prepare_config.assert_called_once_with(config)
        assert result == {"name": "Test", "provider": "mock", "volume": 75}

    def test_prepare_player_config_unknown_provider(self, populated_registry):
        """Test prepare_player_config returns config as-is for unknown provider."""
        config = {"name": "Test", "provider": "unknown"}
        result = populated_registry.prepare_player_config(config)

        assert result == config

    def test_prepare_player_config_fallback_to_defaults(self, registry):
        """Test prepare_player_config merges with defaults when no prepare method."""
        # Use spec=PlayerProvider since PlayerProvider doesn't define prepare_config
        # This means hasattr(provider, 'prepare_config') will return False
        provider = Mock(spec=PlayerProvider)
        provider.get_default_config.return_value = {"provider": "test", "volume": 50}
        registry.register_instance("test", provider)

        config = {"name": "Test", "provider": "test"}
        result = registry.prepare_player_config(config)

        assert result["name"] == "Test"
        assert result["provider"] == "test"
        assert result["volume"] == 50

    def test_prepare_player_config_uses_default_provider(self, registry, mock_provider):
        """Test prepare_player_config uses default provider when not specified."""
        registry.register_instance("squeezelite", mock_provider)
        config = {"name": "Test"}
        result = registry.prepare_player_config(config)

        mock_provider.prepare_config.assert_called_once_with(config)


# =============================================================================
# TEST CLEAR
# =============================================================================


class TestClear:
    """Tests for clear method."""

    def test_clear_removes_all_providers(self, populated_registry):
        """Test clear removes all registered providers."""
        assert len(populated_registry._providers) == 2
        populated_registry.clear()
        assert len(populated_registry._providers) == 0

    def test_clear_empty_registry(self, registry):
        """Test clear on empty registry does not raise."""
        registry.clear()
        assert len(registry._providers) == 0

    def test_clear_preserves_default_provider_setting(self, populated_registry):
        """Test clear preserves default_provider attribute."""
        populated_registry.default_provider = "custom"
        populated_registry.clear()
        assert populated_registry.default_provider == "custom"

    def test_clear_allows_reregistration(self, populated_registry, mock_provider):
        """Test that providers can be re-registered after clear."""
        populated_registry.clear()
        populated_registry.register_instance("new_provider", mock_provider)
        assert populated_registry.has_provider("new_provider")


# =============================================================================
# TEST DEFAULT_PROVIDER CONSTANT
# =============================================================================


class TestDefaultProviderConstant:
    """Tests for DEFAULT_PROVIDER module constant."""

    def test_default_provider_constant_value(self):
        """Test DEFAULT_PROVIDER constant is 'squeezelite'."""
        assert DEFAULT_PROVIDER == "squeezelite"

    def test_registry_uses_constant(self, registry):
        """Test registry uses DEFAULT_PROVIDER constant."""
        assert registry.default_provider == DEFAULT_PROVIDER


# =============================================================================
# TEST EDGE CASES
# =============================================================================


class TestEdgeCases:
    """Tests for edge cases and unusual scenarios."""

    def test_provider_type_with_special_characters(self, registry, mock_provider):
        """Test registering provider with special characters in type."""
        registry.register_instance("test-provider_v2.0", mock_provider)
        assert registry.has_provider("test-provider_v2.0")
        assert registry.get("test-provider_v2.0") == mock_provider

    def test_empty_provider_type(self, registry, mock_provider):
        """Test registering provider with empty string type."""
        registry.register_instance("", mock_provider)
        assert registry.has_provider("")
        assert registry.get("") == mock_provider

    def test_provider_lookup_after_modification(self, registry, mock_provider):
        """Test provider lookup remains consistent after modifications."""
        registry.register_instance("mock", mock_provider)
        first_lookup = registry.get("mock")

        another_provider = Mock(spec=PlayerProvider)
        registry.register_instance("another", another_provider)

        second_lookup = registry.get("mock")
        assert first_lookup == second_lookup == mock_provider

    def test_concurrent_registration_overwrite(self, registry, mock_provider):
        """Test that last registration wins for same type."""
        provider1 = Mock(spec=PlayerProvider)
        provider2 = Mock(spec=PlayerProvider)
        provider3 = mock_provider

        registry.register_instance("same", provider1)
        registry.register_instance("same", provider2)
        registry.register_instance("same", provider3)

        assert registry.get("same") == provider3

    def test_get_for_player_with_none_provider_value(self, registry, mock_provider):
        """Test get_for_player when provider key exists but is None."""
        registry.register_instance("squeezelite", mock_provider)
        config = {"name": "Test", "provider": None}
        result = registry.get_for_player(config)
        assert result is None

    def test_validate_with_exception_in_provider(self, populated_registry, mock_provider):
        """Test validate_player_config when provider raises exception."""
        mock_provider.validate_config.side_effect = ValueError("Unexpected error")
        config = {"name": "Test", "provider": "mock"}

        with pytest.raises(ValueError, match="Unexpected error"):
            populated_registry.validate_player_config(config)


# =============================================================================
# TEST INTEGRATION WITH REAL PROVIDER CLASSES
# =============================================================================


class TestRealProviderIntegration:
    """Integration tests with actual provider implementations."""

    def test_register_squeezelite_provider(self, registry):
        """Test registering actual SqueezeliteProvider."""
        from providers.squeezelite import SqueezeliteProvider

        audio_manager = Mock()
        provider = SqueezeliteProvider(audio_manager)
        registry.register_instance("squeezelite", provider)

        assert registry.has_provider("squeezelite")
        assert registry.get("squeezelite") == provider

    def test_register_sendspin_provider(self, registry):
        """Test registering actual SendspinProvider."""
        from providers.sendspin import SendspinProvider

        audio_manager = Mock()
        provider = SendspinProvider(audio_manager)
        registry.register_instance("sendspin", provider)

        assert registry.has_provider("sendspin")
        assert registry.get("sendspin") == provider

    def test_register_snapcast_provider(self, registry):
        """Test registering actual SnapcastProvider."""
        from providers.snapcast import SnapcastProvider

        audio_manager = Mock()
        provider = SnapcastProvider(audio_manager)
        registry.register_instance("snapcast", provider)

        assert registry.has_provider("snapcast")
        assert registry.get("snapcast") == provider

    def test_multiple_real_providers(self, registry):
        """Test registry with all real providers."""
        from providers.sendspin import SendspinProvider
        from providers.snapcast import SnapcastProvider
        from providers.squeezelite import SqueezeliteProvider

        audio_manager = Mock()

        registry.register_instance("squeezelite", SqueezeliteProvider(audio_manager))
        registry.register_instance("sendspin", SendspinProvider(audio_manager))
        registry.register_instance("snapcast", SnapcastProvider(audio_manager))

        providers = registry.list_providers()
        assert len(providers) == 3
        assert set(providers) == {"squeezelite", "sendspin", "snapcast"}

    def test_validate_with_real_provider(self, registry):
        """Test validation with actual provider implementation."""
        from providers.squeezelite import SqueezeliteProvider

        audio_manager = Mock()
        provider = SqueezeliteProvider(audio_manager)
        registry.register_instance("squeezelite", provider)

        valid_config = {"name": "Kitchen", "device": "hw:0,0", "provider": "squeezelite"}
        is_valid, error = registry.validate_player_config(valid_config)
        assert is_valid is True

        invalid_config = {"provider": "squeezelite"}
        is_valid, error = registry.validate_player_config(invalid_config)
        assert is_valid is False
        assert "name" in error.lower()
