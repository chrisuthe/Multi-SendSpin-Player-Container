# Unit Tests for Multi-Room Audio Controller

This directory contains comprehensive unit tests for the Multi-Room Audio Controller application.

## Test Structure

```
tests/
├── conftest.py                          # Shared pytest fixtures
├── test_api_endpoints.py                # API route integration tests
├── test_audio_manager.py                # AudioManager unit tests
├── test_config_manager.py               # ConfigManager unit tests
├── test_env_validation.py               # Environment validation tests
├── test_player_config_schema.py         # Pydantic schema validation tests
├── test_process_manager.py              # ProcessManager unit tests
├── test_provider_registry.py            # ProviderRegistry unit tests
├── test_sendspin_provider.py            # Sendspin provider tests
├── test_snapcast_provider.py            # Snapcast provider tests
├── test_squeezelite_provider.py         # Squeezelite provider tests
└── README.md                            # This file
```

## Running Tests

### Prerequisites

Install the test dependencies:

```bash
pip install -r requirements.txt
```

This will install pytest and pytest-mock along with the application dependencies.

### Run All Tests

```bash
# Run all tests with verbose output
pytest tests/ -v

# Or use the shorter form
pytest
```

### Run Specific Test Files

```bash
# Test only schema validation
pytest tests/test_player_config_schema.py

# Test only ConfigManager
pytest tests/test_config_manager.py

# Test only AudioManager
pytest tests/test_audio_manager.py

# Test only ProcessManager
pytest tests/test_process_manager.py

# Test only SnapcastProvider
pytest tests/test_snapcast_provider.py
```

### Run Specific Test Classes or Functions

```bash
# Run a specific test class
pytest tests/test_config_manager.py::TestConfigManagerInit

# Run a specific test function
pytest tests/test_config_manager.py::TestConfigManagerInit::test_init_creates_directory
```

### Common pytest Options

```bash
# Show extra test summary info
pytest -ra

# Stop after first failure
pytest -x

# Stop after N failures
pytest --maxfail=3

# Show local variables in tracebacks
pytest -l

# Run tests in parallel (requires pytest-xdist)
pytest -n auto

# Quiet mode (less verbose)
pytest -q

# Very verbose (show all test details)
pytest -vv

# Show print statements
pytest -s

# With coverage report
pytest tests/ --cov=app --cov-report=html
```

## Test Coverage

The test suite covers:

### 1. Pydantic Schema Validation (`test_player_config_schema.py`)
- Base player configuration validation
- Squeezelite player configuration
- Sendspin player configuration
- Snapcast player configuration
- Field validators (name, MAC address, URLs, log levels, etc.)
- Default values and type coercion
- Validation functions and error handling

### 2. ConfigManager (`test_config_manager.py`)
- Configuration file loading and saving
- YAML parsing and error handling
- Player CRUD operations (Create, Read, Update, Delete)
- Configuration validation on load/save
- Player renaming and existence checks
- Error handling for invalid configurations

### 3. AudioManager (`test_audio_manager.py`)
- Audio device detection (hardware and virtual)
- Mixer control enumeration
- Volume get/set operations
- Windows compatibility mode
- Error handling for missing tools (aplay, amixer)
- Virtual device handling

### 4. ProcessManager (`test_process_manager.py`)
- Process lifecycle management (start, stop)
- Process status checking
- Graceful shutdown (SIGTERM) and force kill (SIGKILL)
- Fallback command handling
- Process cleanup and monitoring
- Error handling for process failures

### 5. SnapcastProvider (`test_snapcast_provider.py`)
- Command building with various configurations
- Host ID generation from player names
- Volume control integration
- Configuration validation
- Default configuration values
- Server IP and latency options

## Provider Test Coverage

| Provider | Test File | Key Tests |
|----------|-----------|-----------|
| Squeezelite | `test_squeezelite_provider.py` | MAC generation, buffer params, command building |
| Sendspin | `test_sendspin_provider.py` | PortAudio device index, server URL, delay_ms |
| Snapcast | `test_snapcast_provider.py` | Host ID generation, latency, auto-discovery |

Schema validation for all providers is covered in `test_player_config_schema.py`.

## Mocking Strategy

The tests use extensive mocking to avoid:
- File system I/O (using `tmp_path` fixture and mocked file operations)
- Subprocess calls (mocking `subprocess.run` and `subprocess.Popen`)
- System-specific operations (ALSA tools, process signals)
- Environment detection (mocking `is_hassio()` for HAOS tests)

This ensures tests:
- Run quickly and reliably
- Work on any platform (including Windows)
- Don't require actual audio hardware
- Don't create/modify files outside test directories
- Don't spawn actual processes

## Fixtures

Key fixtures provided in `conftest.py`:

### Configuration Fixtures
- `temp_config_file` - Temporary config file path
- `temp_log_dir` - Temporary log directory
- `sample_squeezelite_config` - Valid Squeezelite configuration
- `sample_sendspin_config` - Valid Sendspin configuration
- `sample_snapcast_config` - Valid Snapcast configuration
- `minimal_*_config` - Minimal valid configurations

### Invalid Configuration Fixtures
- `invalid_config_missing_name` - Missing required name
- `invalid_config_invalid_volume` - Volume out of range
- `invalid_config_bad_mac` - Malformed MAC address
- And more...

### Mock Fixtures
- `mock_aplay_output` - Simulated aplay output
- `mock_amixer_*_output` - Simulated amixer outputs
- `mock_process` - Mock running process
- `mock_failed_process` - Mock failed process

## Environment-Specific Testing

The application supports both standalone Docker (ALSA) and HAOS (PulseAudio) environments. When testing environment-specific code:

```python
# Mock HAOS environment
with patch('environment.is_hassio', return_value=True):
    # Test PulseAudio behavior
    pass

# Mock standalone Docker environment
with patch('environment.is_hassio', return_value=False):
    # Test ALSA behavior
    pass
```

## Known Platform Issues

### Windows
Some tests involving `os.setsid()` may fail on Windows as this is a Unix-specific function. These failures are expected and don't affect the actual application functionality on Linux/Docker.

## Continuous Integration

These tests are designed to run in CI/CD pipelines. The pytest configuration in `pyproject.toml` includes:
- Strict marker checking
- Maximum failure limits
- Short traceback format
- Summary of all test outcomes

## Contributing

When adding new features:

1. Write tests first (TDD approach)
2. Ensure all tests pass: `pytest`
3. Check code style: `ruff check tests/`
4. Format code: `ruff format tests/`
5. Aim for high test coverage

### Adding Provider Tests

When adding a new provider, create a corresponding test file:

```bash
tests/test_newprovider_provider.py
```

Include tests for:
- `build_command()` - All command variations
- `validate_config()` - Valid and invalid configurations
- `prepare_config()` - Default value generation
- `get_volume()` / `set_volume()` - Volume control
- Any unique identifier generation (MAC, host ID, etc.)

## Troubleshooting

### Import Errors

If you see import errors, ensure you're running pytest from the project root:

```bash
cd /path/to/squeezelite-docker
pytest tests/
```

The `conftest.py` adds the `app/` directory to Python's path automatically.

### Missing Dependencies

If tests fail due to missing packages:

```bash
pip install -r requirements.txt
```

### Platform-Specific Issues

Tests are designed to be platform-agnostic. If you encounter platform-specific issues:
- Check that mocking is properly configured
- Ensure subprocess calls are mocked
- Verify file paths use `os.path.join()` or `Path`
- Mock `os.setsid` on Windows if needed
