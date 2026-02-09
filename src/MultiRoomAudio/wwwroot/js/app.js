// Multi-Room Audio Controller - JavaScript App

// State
let players = {};
let devices = [];
let formats = [];
let advancedFormatsEnabled = false;
let connection = null;
let currentBuildVersion = null; // Stored build version for comparison
let isUserInteracting = false; // Track if user is dragging a slider
let pendingUpdate = null; // Store pending updates during interaction

// Debounced volume change for real-time slider updates
let volumeDebounceTimers = {}; // Per-player debounce timers
function setVolumeDebounced(name, volume) {
    // Clear existing timer for this player
    if (volumeDebounceTimers[name]) {
        clearTimeout(volumeDebounceTimers[name]);
    }
    // Set new timer - 100ms debounce for responsive feel without flooding API
    volumeDebounceTimers[name] = setTimeout(() => {
        setVolume(name, volume);
        delete volumeDebounceTimers[name];
    }, 100);
}

/**
 * Check if user is actively interacting with player tiles.
 * Only checks for transient interactions that have clear start/end:
 * - Volume slider is being dragged
 * - Dropdown menu is open on a player card
 */
function isUserInteractingWithPlayers() {
    // Slider drag in progress
    if (isUserInteracting) return true;

    // Any dropdown open on a player card
    if (document.querySelector('.player-card .dropdown-menu.show')) return true;

    return false;
}
let isModalOpen = false; // Pause auto-refresh while modal is open
let serverAvailable = true; // Track whether the backend is reachable
let disconnectedSince = null; // Timestamp when server became unavailable
let gracefulShutdown = false; // True when server sent explicit shutdown notice
let startupComplete = false; // Track whether backend startup is finished

function formatBuildVersion(apiInfo) {
    const version = apiInfo?.version;
    if (typeof version === 'string' && version.trim()) {
        return version.trim();
    }

    const build = apiInfo?.build;
    if (typeof build === 'string' && build.trim()) {
        const trimmedBuild = build.trim();
        if (trimmedBuild.startsWith('sha-')) {
            return trimmedBuild;
        }
        return `sha-${trimmedBuild.slice(0, 7)}`;
    }

    return 'unknown';
}

async function refreshBuildInfo() {
    const buildVersion = document.getElementById('build-version');
    if (!buildVersion) return;

    try {
        const response = await fetch('./api');
        if (!response.ok) throw new Error('Failed to fetch build info');

        const data = await response.json();
        const formattedVersion = formatBuildVersion(data);
        buildVersion.textContent = formattedVersion;
        buildVersion.title = data?.build ? `Full build: ${data.build}` : '';

        // Store the full build string for version comparison
        if (currentBuildVersion === null) {
            // First load - store the version
            currentBuildVersion = data?.build || formattedVersion;
            console.log('Initial build version:', currentBuildVersion);
        }
    } catch (error) {
        console.error('Error fetching build info:', error);
        buildVersion.textContent = 'unknown';
        buildVersion.title = '';
    }
}

/**
 * Checks if the backend version has changed and reloads the page if needed.
 * Called on SignalR reconnect and periodically as a fallback.
 */
async function checkVersionAndReload() {
    try {
        const response = await fetch('./api');
        if (!response.ok) return; // Silently fail - backend might be starting

        const data = await response.json();
        const serverVersion = data?.build || formatBuildVersion(data);

        // If we have a stored version and it differs from server, reload
        if (currentBuildVersion !== null && currentBuildVersion !== serverVersion) {
            console.log(`Version changed: ${currentBuildVersion} → ${serverVersion}. Reloading page...`);
            showAlert('Backend updated - reloading page...', 'info', 2000);

            // Wait 2 seconds to show the alert, then reload
            setTimeout(() => {
                window.location.reload(true); // Hard reload (bypass cache)
            }, 2000);
        }
    } catch (error) {
        // Silently fail - backend might be restarting
        console.debug('Version check failed (backend might be restarting):', error);
    }
}

/**
 * Sets the server availability state and updates the UI accordingly.
 * When unavailable: dims player cards, shows reconnecting banner, disables controls.
 * When available again: removes overlay, hides banner, refreshes state.
 * @param {boolean} available - Whether the server is reachable
 */
function setServerAvailable(available) {
    if (serverAvailable === available) return; // No change
    serverAvailable = available;

    const playersContainer = document.getElementById('players-container');
    const banner = document.getElementById('reconnect-banner');
    const bannerText = document.getElementById('reconnect-banner-text');
    const elapsedEl = document.getElementById('reconnect-elapsed');

    if (!available) {
        // Server went away — reset startup tracking so restart shows progress
        startupComplete = false;
        disconnectedSince = Date.now();
        playersContainer.classList.add('server-unavailable');
        banner.classList.add('visible');
        if (bannerText) {
            bannerText.textContent = gracefulShutdown
                ? 'Server shutting down — waiting for restart...'
                : 'Server unavailable — reconnecting...';
        }
        updateReconnectElapsed(); // Start elapsed timer
    } else {
        // Server is back
        const downtimeSec = disconnectedSince ? Math.round((Date.now() - disconnectedSince) / 1000) : 0;
        disconnectedSince = null;
        gracefulShutdown = false;
        playersContainer.classList.remove('server-unavailable');
        banner.classList.remove('visible');
        if (elapsedEl) elapsedEl.textContent = '';

        // Update connection badge immediately (don't wait for SignalR handshake)
        const statusBadge = document.getElementById('connection-status');
        if (statusBadge) {
            statusBadge.textContent = 'Connected';
            statusBadge.className = 'badge bg-success me-2';
        }

        console.log(`Server reconnected after ${downtimeSec}s`);

        // Check if backend startup is still in progress before loading data
        checkStartupAndRecover();
    }
}

/**
 * Checks startup status after reconnection. If backend is still initializing,
 * shows the startup overlay. Otherwise does a full data refresh.
 */
async function checkStartupAndRecover() {
    try {
        const response = await fetch('./api/startup');
        if (response.ok) {
            const progress = await response.json();
            if (!progress.complete) {
                // Backend still starting — show startup overlay, let SignalR events handle transition
                console.log('Server is back but still starting — showing startup overlay');
                renderStartupProgress(progress);
                return;
            }
        }
    } catch {
        // Fetch failed — fall through to normal recovery
    }

    // Startup already complete (or couldn't check) — normal recovery
    startupComplete = true;
    setStartupOverlayVisible(false);

    showAlert('Reconnected to server', 'success', 3000);
    refreshStatus(true);
    refreshDevices();
    checkVersionAndReload();
}

/**
 * Updates the elapsed time shown in the reconnect banner.
 * Runs every second while disconnected.
 */
function updateReconnectElapsed() {
    if (!disconnectedSince) return;

    const elapsedEl = document.getElementById('reconnect-elapsed');
    if (elapsedEl) {
        const seconds = Math.round((Date.now() - disconnectedSince) / 1000);
        if (seconds < 60) {
            elapsedEl.textContent = `(${seconds}s)`;
        } else {
            const mins = Math.floor(seconds / 60);
            const secs = seconds % 60;
            elapsedEl.textContent = `(${mins}m ${secs}s)`;
        }
    }

    setTimeout(updateReconnectElapsed, 1000);
}

/**
 * Shows or hides the startup overlay modal.
 * Content remains visible behind the semi-transparent backdrop.
 * @param {boolean} visible - Whether the overlay should be visible
 */
function setStartupOverlayVisible(visible) {
    const overlay = document.getElementById('startup-overlay');
    if (overlay) {
        if (visible) {
            overlay.classList.remove('hidden');
        } else {
            overlay.classList.add('hidden');
        }
    }
}

/**
 * Renders the startup progress overlay.
 * Shows phase list with status icons. Hides overlay when startup completes.
 * @param {object} progress - { complete: bool, phases: [{ id, name, status, detail }] }
 */
function renderStartupProgress(progress) {
    const overlay = document.getElementById('startup-overlay');
    const phasesEl = document.getElementById('startup-phases');
    if (!overlay || !phasesEl) return;

    if (progress.complete) {
        setStartupOverlayVisible(false);
        return;
    }

    setStartupOverlayVisible(true);

    // Build phase list using DOM manipulation
    phasesEl.innerHTML = '';
    for (const phase of progress.phases) {
        const row = document.createElement('div');
        row.className = 'startup-phase';

        const icon = document.createElement('span');
        icon.className = 'phase-icon';

        if (phase.status === 'InProgress') {
            row.classList.add('active');
            icon.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
        } else if (phase.status === 'Completed') {
            row.classList.add('completed');
            icon.innerHTML = '<i class="fas fa-check-circle"></i>';
        } else if (phase.status === 'Failed') {
            row.classList.add('failed');
            icon.innerHTML = '<i class="fas fa-times-circle"></i>';
        } else {
            // Pending
            icon.innerHTML = '<i class="fas fa-circle" style="font-size: 0.5rem;"></i>';
        }

        const label = document.createElement('span');
        label.textContent = phase.name;

        row.appendChild(icon);
        row.appendChild(label);

        if (phase.detail) {
            const detail = document.createElement('span');
            detail.className = 'phase-detail';
            detail.textContent = `— ${phase.detail}`;
            row.appendChild(detail);
        }

        phasesEl.appendChild(row);
    }
}

/**
 * Get display name for a device ID.
 * Returns alias if available, otherwise name, otherwise the ID itself.
 * @param {string} deviceId - The device ID (sink name)
 * @returns {string} Human-readable device name
 */
function getDeviceDisplayName(deviceId) {
    if (!deviceId) return 'Default';

    const device = devices.find(d => d.id === deviceId);
    if (device) {
        return device.alias || device.name || deviceId;
    }
    return deviceId;  // Fallback to ID if device not found
}

/**
 * Get detailed connection info for a device ID.
 * Returns formatted string with Sink/Device prefix and description/alias.
 * @param {string} deviceId - The device ID (sink name)
 * @returns {string} Formatted connection info (e.g., "Sink: zone1 (Left Channel)")
 */
function getConnectionInfo(deviceId) {
    if (!deviceId) return 'Device: Default';

    const device = devices.find(d => d.id === deviceId);
    if (!device) return deviceId;

    // Determine if this is a custom sink or hardware device
    if (device.sinkType) {
        // Custom sink: show "Sink: name (description)" or just "Sink: name"
        if (device.alias) {
            return `Sink: ${device.name} (${device.alias})`;
        }
        return `Sink: ${device.name}`;
    } else {
        // Hardware device: use device.name directly (already correct from PulseAudio sink description)
        // (Previous code tried to look up card by cardIndex, but cardIndex is ALSA card number
        // while card.index is PulseAudio card index - different numbering systems!)
        const cardName = device.name;
        // Show "Device: cardName (alias)" or just "Device: cardName"
        if (device.alias) {
            return `Device: ${cardName} (${device.alias})`;
        }
        return `Device: ${cardName}`;
    }
}

// XSS protection
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Escape string for JavaScript single-quoted string literals in onclick handlers
function escapeJsString(str) {
    return str.replace(/\\/g, '\\\\').replace(/'/g, "\\'");
}

// Extract simplified USB port identifier from full device path
// e.g., "...AppleUSB20HubPort@02341200..." -> "Port 4.1.2"
function extractUsbPort(usbPath) {
    if (!usbPath) return null;

    // Look for USB hub port patterns in the path
    // macOS: AppleUSB20HubPort@XXXXXXXX
    const portMatches = usbPath.match(/HubPort@([0-9a-fA-F]+)/g);
    if (portMatches && portMatches.length > 0) {
        // Extract port numbers from hex addresses
        // Format is typically: 0234XYZZ where X=hub port, Y=sub-port, ZZ=00
        // e.g., 02340000=port 0, 02341000=port 1.0, 02341100=port 1.1, 02341200=port 1.2
        const ports = portMatches.map(m => {
            const hex = m.match(/@([0-9a-fA-F]+)/)[1];
            // Extract digits at positions 4 and 5
            const major = parseInt(hex.charAt(4), 16);
            const minor = parseInt(hex.charAt(5), 16);
            return minor > 0 ? `${major}.${minor}` : `${major}`;
        });
        return `Port ${ports.join('-')}`;
    }

    // Linux path pattern: look for usb port numbers like "1-2.3"
    const linuxMatch = usbPath.match(/(\d+-[\d.]+)/);
    if (linuxMatch) {
        return `Port ${linuxMatch[1]}`;
    }

    return null;
}

// Format sample rate for display (e.g., 48000 -> "48kHz", 192000 -> "192kHz")
function formatSampleRate(rate) {
    if (rate >= 1000) {
        return (rate / 1000) + 'kHz';
    }
    return rate + 'Hz';
}

// Format date/time for compact display (e.g., "2/1/26 11:05")
function formatShortDateTime(dateStr) {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    const month = d.getMonth() + 1;
    const day = d.getDate();
    const year = String(d.getFullYear()).slice(-2);
    const hours = d.getHours();
    const mins = String(d.getMinutes()).padStart(2, '0');
    return `${month}/${day}/${year} ${hours}:${mins}`;
}

/**
 * Format a channel name for display (e.g., "front-left" → "Front L")
 * @param {string} channel - The channel name from PulseAudio
 * @returns {string} Human-readable channel name
 */
function formatChannelName(channel) {
    const map = {
        'front-left': 'Front L', 'front-right': 'Front R',
        'rear-left': 'Rear L', 'rear-right': 'Rear R',
        'front-center': 'Center', 'lfe': 'LFE',
        'side-left': 'Side L', 'side-right': 'Side R',
        'mono': 'Mono'
    };
    return map[channel?.toLowerCase()] || channel || 'Unknown';
}

/**
 * Determines the connection type of an audio device from its identifiers and ID.
 * Uses device.bus_path (sysfs path) and device ID for detection:
 * - HDMI: device ID contains "hdmi" (HDMI audio output)
 * - USB: bus_path contains "/usb"
 * - PCI: bus_path contains "/pci" but NOT "/usb" (USB devices are on PCI USB controllers)
 * - Bluetooth: device ID starts with "bluez_" (no sysfs bus_path)
 * @param {object} device - Device object with id and identifiers properties
 * @returns {string} 'hdmi', 'usb', 'pci', 'bluetooth', or 'unknown'
 */
function getDeviceBusType(device) {
    if (!device) return 'unknown';

    const deviceId = device.id || '';

    // Check for HDMI first (HDMI devices are on PCI but should show HDMI icon)
    if (deviceId.toLowerCase().includes('hdmi')) return 'hdmi';

    // Check for Bluetooth (bluez devices don't have sysfs bus_path)
    if (deviceId.startsWith('bluez_')) return 'bluetooth';

    // Check bus_path from identifiers (most reliable for USB vs PCI)
    const busPath = device.identifiers?.busPath;
    if (busPath) {
        if (busPath.includes('/usb')) return 'usb';
        if (busPath.includes('/pci')) return 'pci';
    }

    // Fallback: Check device ID patterns for USB/PCI
    if (deviceId.includes('.usb-')) return 'usb';
    if (deviceId.includes('.pci-')) return 'pci';

    return 'unknown';
}

/**
 * Determines the connection type of a sound card from its name and active profile.
 * Card names follow PulseAudio naming conventions:
 * - HDMI: active profile contains "hdmi" (HDMI-only cards like GPU audio)
 * - USB: "alsa_card.usb-..."
 * - PCI: "alsa_card.pci-..."
 * - Bluetooth: "bluez_card.XX_XX_XX..."
 * @param {string} cardName - The card name from PulseAudio
 * @param {string} [activeProfile] - Optional active profile name for HDMI detection
 * @returns {string} 'hdmi', 'usb', 'pci', 'bluetooth', or 'unknown'
 */
function getCardBusType(cardName, activeProfile) {
    if (!cardName) return 'unknown';
    if (cardName.startsWith('bluez_')) return 'bluetooth';
    if (cardName.includes('.usb-')) return 'usb';
    if (cardName.includes('.pci-')) {
        // Check if this is an HDMI-only card (like GPU audio)
        // by checking if the active profile is HDMI
        if (activeProfile && activeProfile.toLowerCase().includes('hdmi')) {
            return 'hdmi';
        }
        return 'pci';
    }
    return 'unknown';
}

/**
 * Gets the appropriate Font Awesome icon class for a connection type.
 * @param {string} busType - The connection type ('hdmi', 'usb', 'pci', 'bluetooth', or 'unknown')
 * @returns {string} Font Awesome icon class
 */
function getBusTypeIcon(busType) {
    switch (busType) {
        case 'bluetooth': return 'fab fa-bluetooth-b';
        case 'usb': return 'fab fa-usb';
        case 'hdmi': return 'fas fa-tv';  // TV/monitor icon for HDMI
        case 'pci': return 'fas fa-microchip';
        default: return 'fas fa-volume-up';
    }
}

/**
 * Gets a human-readable label for a connection type.
 * @param {string} busType - The connection type ('hdmi', 'usb', 'pci', 'bluetooth', or 'unknown')
 * @returns {string} Human-readable connection type label
 */
function getBusTypeLabel(busType) {
    switch (busType) {
        case 'bluetooth': return 'Bluetooth';
        case 'usb': return 'USB';
        case 'hdmi': return 'HDMI';
        case 'pci': return 'PCI/Internal';
        default: return 'Audio';
    }
}

// Initialize app
document.addEventListener('DOMContentLoaded', async () => {
    // Set up SignalR connection immediately (available before startup completes)
    setupSignalR();

    // Stop all polling when tab is hidden to reduce VM scheduling pressure
    document.addEventListener('visibilitychange', () => {
        if (document.hidden) {
            // Stop stats polling
            if (statsInterval) {
                clearTimeout(statsInterval);
                statsInterval = null;
            }
            // Stop home page polling
            if (pollTimer) {
                clearTimeout(pollTimer);
                pollTimer = null;
            }
        } else {
            // Resume appropriate polling when tab becomes visible
            const statsModal = document.getElementById('statsForNerdsModal');
            if (statsModal && statsModal.classList.contains('show') && currentStatsPlayer) {
                // Stats modal is open - resume stats polling
                fetchAndRenderStats().then(() => {
                    if (currentStatsPlayer) {
                        statsInterval = setTimeout(function pollStats() {
                            fetchAndRenderStats().then(() => {
                                if (currentStatsPlayer) {
                                    statsInterval = setTimeout(pollStats, 2000);
                                }
                            });
                        }, 2000);
                    }
                });
            } else {
                // No modal open - resume home page polling
                schedulePoll();
            }
        }
    });

    // Check startup progress first — show overlay if backend is still initializing
    try {
        const startupResponse = await fetch('./api/startup');
        if (startupResponse.ok) {
            const progress = await startupResponse.json();
            renderStartupProgress(progress);

            if (progress.complete) {
                startupComplete = true;
            }
        } else {
            // Endpoint not available (shouldn't happen) — assume complete
            startupComplete = true;
            setStartupOverlayVisible(false);
        }
    } catch {
        // Server not reachable yet — show overlay, hide main content
        startupComplete = false;
        setStartupOverlayVisible(true);
    }

    // Load data that doesn't depend on startup completion
    await Promise.all([
        checkAdvancedFormats(),
        refreshBuildInfo()
    ]);

    // Only load player/device data if startup is already complete
    if (startupComplete) {
        setStartupOverlayVisible(false);

        // Load devices/cards first so player cards can display connection info correctly
        await refreshDevices();
        await refreshStatus();

        // Check if onboarding wizard should show
        if (typeof Wizard !== 'undefined') {
            const shouldShow = await Wizard.shouldShow();
            if (shouldShow) {
                await Wizard.show();
            }
        }
    }

    // Set up volume slider preview
    const volumeSlider = document.getElementById('initialVolume');
    const volumeValue = document.getElementById('initialVolumeValue');
    if (volumeSlider && volumeValue) {
        volumeSlider.addEventListener('input', () => {
            volumeValue.textContent = volumeSlider.value + '%';
        });
    }

    // Adaptive polling: fast (500ms) when server is unavailable, normal (5s) otherwise
    let pollTimer = null;
    function schedulePoll() {
        const delay = serverAvailable ? 5000 : 500;
        pollTimer = setTimeout(async () => {
            await refreshStatus();
            schedulePoll();
        }, delay);
    }
    schedulePoll();

    // Periodic version check (every 30 seconds) as fallback
    setInterval(checkVersionAndReload, 30000);

    // Apply pending updates when dropdown closes (Bootstrap event)
    document.addEventListener('hidden.bs.dropdown', (event) => {
        // Only care about dropdowns inside player cards
        if (event.target.closest('.player-card') && pendingUpdate) {
            console.log('Dropdown closed - applying pending update');
            players = pendingUpdate.players;
            pendingUpdate = null;
            renderPlayers();
        }
    });

});

// SignalR setup
function setupSignalR() {
    const statusBadge = document.getElementById('connection-status');

    // SignalR is optional - use polling as fallback
    if (typeof signalR === 'undefined') {
        console.log('SignalR not available, using polling');
        statusBadge.textContent = 'Polling';
        statusBadge.className = 'badge bg-info me-2';
        return;
    }

    connection = new signalR.HubConnectionBuilder()
        .withUrl('./hubs/status')
        .withAutomaticReconnect({
            nextRetryDelayInMilliseconds: (retryContext) => {
                // Exponential backoff: 1s, 2s, 4s, 8s, 16s, capped at 30s
                // Retries forever (never returns null)
                return Math.min(1000 * Math.pow(2, retryContext.previousRetryCount), 30000);
            }
        })
        .build();

    connection.on('ServerShuttingDown', () => {
        console.log('Server sent graceful shutdown notification');
        gracefulShutdown = true;
        setServerAvailable(false);
    });

    connection.on('StartupProgress', (progress) => {
        console.log('Startup progress:', progress);
        renderStartupProgress(progress);

        if (progress.complete && !startupComplete) {
            startupComplete = true;
            console.log('Startup complete — loading initial data');

            // Hide overlay and show main content
            setStartupOverlayVisible(false);

            refreshStatus(true);
            refreshDevices();

            // Check onboarding wizard
            if (typeof Wizard !== 'undefined') {
                Wizard.shouldShow().then(shouldShow => {
                    if (shouldShow) Wizard.show();
                });
            }
        }
    });

    connection.on('PlayerStatusUpdate', (data) => {
        console.log('Status update:', data);
        if (data.players) {
            // Convert array to object keyed by name (same as refreshStatus)
            players = {};
            (data.players || []).forEach(p => {
                players[p.name] = p;
            });

            // If user is interacting with player tiles, defer the update
            if (isUserInteractingWithPlayers()) {
                console.log('Deferring update - user is interacting with players');
                pendingUpdate = { players: { ...players } };
            } else {
                renderPlayers();
            }
        }
    });

    connection.on('DeviceListChanged', async () => {
        console.log('Device list changed, refreshing devices...');
        await refreshDevices();
    });

    connection.onreconnecting(() => {
        statusBadge.textContent = 'Reconnecting...';
        statusBadge.className = 'badge bg-warning me-2';
        setServerAvailable(false);

        // Connection dropped — if graceful shutdown, transition message after a short delay
        // so user sees "shutting down..." before it changes to "shut down..."
        if (gracefulShutdown) {
            setTimeout(() => {
                if (gracefulShutdown && !serverAvailable) {
                    const bannerText = document.getElementById('reconnect-banner-text');
                    if (bannerText) bannerText.textContent = 'Server shut down — waiting for restart...';
                }
            }, 3000);
        }
    });

    connection.onreconnected(() => {
        statusBadge.textContent = 'Connected';
        statusBadge.className = 'badge bg-success me-2';
        setServerAvailable(true);
    });

    connection.onclose(() => {
        statusBadge.textContent = 'Disconnected';
        statusBadge.className = 'badge bg-danger me-2';
        setServerAvailable(false);

        if (gracefulShutdown) {
            setTimeout(() => {
                if (gracefulShutdown && !serverAvailable) {
                    const bannerText = document.getElementById('reconnect-banner-text');
                    if (bannerText) bannerText.textContent = 'Server shut down — waiting for restart...';
                }
            }, 3000);
        }
    });

    connection.start()
        .then(async () => {
            statusBadge.textContent = 'Connected';
            statusBadge.className = 'badge bg-success me-2';

            // Re-fetch startup status in case we missed SignalR broadcasts during connection
            // (startup phases may have completed before SignalR was connected)
            if (!startupComplete) {
                try {
                    const response = await fetch('./api/startup');
                    if (response.ok) {
                        const progress = await response.json();
                        renderStartupProgress(progress);
                        if (progress.complete) {
                            startupComplete = true;
                            refreshStatus(true);
                            refreshDevices();
                            if (typeof Wizard !== 'undefined') {
                                Wizard.shouldShow().then(shouldShow => {
                                    if (shouldShow) Wizard.show();
                                });
                            }
                        }
                    }
                } catch { /* ignore - will retry via polling */ }
            }
        })
        .catch(err => {
            console.log('SignalR connection failed, using polling:', err);
            statusBadge.textContent = 'Polling';
            statusBadge.className = 'badge bg-info me-2';
        });
}

// API calls
async function refreshStatus(force = false, manual = false) {
    // Skip auto-refresh while modal is open (unless forced)
    if (isModalOpen && !force) {
        return;
    }

    // Show spinner on manual refresh (user clicked button)
    const refreshBtn = document.querySelector('[onclick="refreshStatus(true, true)"]');
    let originalContent = null;
    if (manual && refreshBtn) {
        originalContent = refreshBtn.innerHTML;
        refreshBtn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>Refreshing...';
        refreshBtn.disabled = true;
    }

    try {
        // If manual refresh, also refresh devices and cards
        if (manual) {
            const [devicesRes, cardsRes, playersRes] = await Promise.all([
                fetch('./api/devices/refresh', { method: 'POST' }),
                fetch('./api/cards'),
                fetch('./api/players')
            ]);

            if (!playersRes.ok) throw new Error('Failed to fetch players');

            const devicesData = devicesRes.ok ? await devicesRes.json() : null;
            const cardsData = cardsRes.ok ? await cardsRes.json() : null;
            const playersData = await playersRes.json();

            // Update players
            players = {};
            (playersData.players || []).forEach(p => {
                players[p.name] = p;
            });

            // Defer DOM update if user is interacting with player tiles
            if (isUserInteractingWithPlayers()) {
                pendingUpdate = { players: { ...players } };
            } else {
                renderPlayers();
            }

            // Show toast - warn if device refresh failed
            const playerCount = Object.keys(players).length;
            if (!devicesRes.ok) {
                showAlert(`Refreshed ${playerCount} players, but device scan failed`, 'warning', 4000);
            } else {
                const deviceCount = devicesData?.count ?? '?';
                const cardCount = cardsData?.cards?.length ?? '?';
                showAlert(`Refreshed: ${playerCount} players, ${deviceCount} devices, ${cardCount} cards`, 'success', 3000);
            }
        } else {
            // Auto-refresh or force-refresh: just fetch players (existing behavior)
            const response = await fetch('./api/players');
            if (!response.ok) throw new Error('Failed to fetch players');

            const data = await response.json();
            players = {};
            (data.players || []).forEach(p => {
                players[p.name] = p;
            });

            // Defer DOM update if user is interacting with player tiles
            if (isUserInteractingWithPlayers()) {
                pendingUpdate = { players: { ...players } };
            } else {
                renderPlayers();
            }
        }

        // Server responded successfully — if it was previously unavailable, recover
        if (!serverAvailable) {
            setServerAvailable(true);

            // Restart SignalR if not connected (server restart invalidates old connection)
            if (connection && connection.state !== signalR.HubConnectionState.Connected) {
                console.log(`Server is back — restarting SignalR (was ${connection.state})`);
                const statusBadge = document.getElementById('connection-status');
                // Stop the stale connection (cancels any in-flight reconnect attempts)
                connection.stop()
                    .catch(() => {})
                    .then(() => connection.start())
                    .then(() => {
                        statusBadge.textContent = 'Connected';
                        statusBadge.className = 'badge bg-success me-2';
                    })
                    .catch(err => {
                        console.log('SignalR restart failed, will retry on next poll:', err);
                    });
            }
        }
    } catch (error) {
        console.error('Error refreshing status:', error);

        if (manual) {
            showAlert('Refresh failed: ' + error.message, 'danger', 5000);
        }

        // During startup, errors are expected — don't trigger server-unavailable state
        if (!startupComplete) return;

        // Mark server as unavailable (only on polling failures, not forced refresh)
        if (!force && serverAvailable) {
            setServerAvailable(false);
        }
    } finally {
        // Restore button state
        if (manual && refreshBtn && originalContent) {
            refreshBtn.innerHTML = originalContent;
            refreshBtn.disabled = false;
        }
    }
}

async function checkAdvancedFormats() {
    try {
        const response = await fetch('./api/players/formats');
        advancedFormatsEnabled = response.ok;

        if (advancedFormatsEnabled) {
            document.getElementById('advertisedFormatGroup').style.display = 'block';
        }
    } catch (error) {
        advancedFormatsEnabled = false;
    }
}

async function refreshFormats() {
    if (!advancedFormatsEnabled) return;

    try {
        const response = await fetch('./api/players/formats');
        if (!response.ok) throw new Error('Failed to fetch formats');

        const data = await response.json();
        formats = data.formats || [];

        const formatSelect = document.getElementById('advertisedFormat');
        if (formatSelect) {
            const currentValue = formatSelect.value;
            formatSelect.innerHTML = '';
            formats.forEach(format => {
                const option = document.createElement('option');
                option.value = format.id;
                option.textContent = format.label;
                option.title = format.description;
                formatSelect.appendChild(option);
            });
            if (currentValue) formatSelect.value = currentValue;
        }
    } catch (error) {
        console.error('Error refreshing formats:', error);
    }
}

async function refreshDevices(currentDeviceId = null) {
    try {
        // Fetch devices, cards, and sinks in parallel
        const [devicesResponse, cardsResponse, sinksResponse] = await Promise.all([
            fetch('./api/devices'),
            fetch('./api/cards'),
            fetch('./api/sinks')
        ]);

        if (!devicesResponse.ok) throw new Error('Failed to fetch devices');

        const devicesData = await devicesResponse.json();
        devices = devicesData.devices || [];

        // Build card description map if cards request succeeded
        let cardDescriptions = new Map(); // cardIndex -> description
        if (cardsResponse.ok) {
            const cardsData = await cardsResponse.json();
            const cards = cardsData.cards || [];
            // Update global soundCards for use by getConnectionInfo()
            soundCards = cards;
            // Build map of card index to description
            cards.forEach(c => {
                cardDescriptions.set(c.index, c.description || c.name);
            });
        }

        // Get remap master sinks to auto-hide from player creation
        let remapMasterSinks = new Set();
        if (sinksResponse.ok) {
            const sinksData = await sinksResponse.json();
            const sinks = sinksData.sinks || [];
            // Collect all masterSink values from loaded remap sinks
            sinks.forEach(sink => {
                if (sink.type === 'Remap' && sink.state === 'Loaded' && sink.masterSink) {
                    remapMasterSinks.add(sink.masterSink);
                }
            });
        }

        // Filter devices for player creation:
        // - Exclude devices with hidden = true (manually hidden via wizard)
        // - Exclude devices that are remap masters (auto-hidden)
        // - But always include currentDeviceId if provided (for edit mode)
        const visibleDevices = devices.filter(device => {
            // Always include current device when editing
            if (currentDeviceId && device.id === currentDeviceId) return true;
            // Filter out manually hidden devices
            if (device.hidden) return false;
            // Filter out remap master sinks
            if (remapMasterSinks.has(device.id)) return false;
            return true;
        });

        // Update device selects
        const selects = document.querySelectorAll('#audioDevice, #editAudioDevice');
        selects.forEach(select => {
            const currentValue = select.value;
            select.innerHTML = '<option value="" disabled selected>Select a device...</option>';
            visibleDevices.forEach(device => {
                const option = document.createElement('option');
                option.value = device.id;
                // Format display name based on device type with Sink:/Device: prefix
                let displayName;
                if (device.sinkType) {
                    // Custom sink: show "Sink: name (description)" if description exists
                    displayName = device.alias ? `Sink: ${device.name} (${device.alias})` : `Sink: ${device.name}`;
                } else {
                    // Hardware device: use device.name directly (already correct from PulseAudio)
                    // (cardDescriptions map uses PulseAudio index but device.cardIndex is ALSA card number)
                    const cardName = device.name;
                    displayName = device.alias ? `Device: ${cardName} (${device.alias})` : `Device: ${cardName}`;
                }
                if (device.isDefault) displayName += ' (default)';
                // Mark hidden/remap devices when shown for edit
                if (currentDeviceId && device.id === currentDeviceId) {
                    if (device.hidden) displayName += ' (hidden)';
                    else if (remapMasterSinks.has(device.id)) displayName += ' (remap master)';
                }
                option.textContent = displayName;
                select.appendChild(option);
            });
            if (currentValue) select.value = currentValue;
        });
    } catch (error) {
        console.error('Error refreshing devices:', error);
    }
}

// Open the modal in Add mode
async function openAddPlayerModal() {
    isModalOpen = true;

    // Reset form
    document.getElementById('playerForm').reset();
    document.getElementById('editingPlayerName').value = '';
    document.getElementById('initialVolumeValue').textContent = '75%';
    document.getElementById('autoResume').checked = false; // Default to off for new players

    // Set modal to Add mode
    document.getElementById('playerModalIcon').className = 'fas fa-plus-circle me-2';
    document.getElementById('playerModalTitleText').textContent = 'Add New Player';
    document.getElementById('playerModalSubmitIcon').className = 'fas fa-plus me-1';
    document.getElementById('playerModalSubmitText').textContent = 'Add Player';

    // Refresh devices and formats
    await refreshDevices();
    if (advancedFormatsEnabled) {
        await refreshFormats();
    }

    const modal = new bootstrap.Modal(document.getElementById('playerModal'));
    modal.show();

    // Reset flag when modal closes
    document.getElementById('playerModal').addEventListener('hidden.bs.modal', () => {
        isModalOpen = false;
    }, { once: true });
}

// Open the modal in Edit mode with player data
async function openEditPlayerModal(playerName) {
    isModalOpen = true;

    try {
        // Fetch current player data
        const response = await fetch(`./api/players/${encodeURIComponent(playerName)}`);
        if (!response.ok) {
            throw new Error('Failed to fetch player data');
        }
        const player = await response.json();

        // Reset form first
        document.getElementById('playerForm').reset();

        // Store original name for update logic
        document.getElementById('editingPlayerName').value = playerName;

        // Populate form with current values
        document.getElementById('playerName').value = player.name;
        document.getElementById('serverUrl').value = player.serverUrl || '';
        document.getElementById('initialVolume').value = player.startupVolume;
        document.getElementById('initialVolumeValue').textContent = player.startupVolume + '%';
        document.getElementById('autoResume').checked = player.autoResume || false;

        // Set device dropdown (pass current device to include it even if hidden/remap master)
        await refreshDevices(player.device);
        const audioDeviceSelect = document.getElementById('audioDevice');
        if (player.device) {
            audioDeviceSelect.value = player.device;
            // Check if the device was actually found in the list
            if (audioDeviceSelect.value !== player.device) {
                showAlert(`Warning: Previously configured device "${player.device}" is no longer available. Please select a new device.`, 'warning');
            }
        }

        // Set advertised format dropdown (if advanced formats enabled)
        if (advancedFormatsEnabled) {
            // Refresh formats first to populate options
            await refreshFormats();

            // Store original format for change detection (default to flac-48000 for compatibility)
            const originalFormat = player.advertisedFormat || 'flac-48000';
            document.getElementById('playerForm').dataset.originalFormat = originalFormat;

            // Set dropdown value AFTER options are populated
            const formatSelect = document.getElementById('advertisedFormat');
            if (formatSelect) {
                formatSelect.value = originalFormat;
            }
        }

        // Set modal to Edit mode
        document.getElementById('playerModalIcon').className = 'fas fa-edit me-2';
        document.getElementById('playerModalTitleText').textContent = 'Edit Player';
        document.getElementById('playerModalSubmitIcon').className = 'fas fa-save me-1';
        document.getElementById('playerModalSubmitText').textContent = 'Save Changes';

        const modal = new bootstrap.Modal(document.getElementById('playerModal'));
        modal.show();

        // Reset flag when modal closes
        document.getElementById('playerModal').addEventListener('hidden.bs.modal', () => {
            isModalOpen = false;
        }, { once: true });
    } catch (error) {
        isModalOpen = false;
        console.error('Error opening edit modal:', error);
        showAlert(error.message, 'danger');
    }
}

// Save player (handles both add and edit)
async function savePlayer() {
    const editingName = document.getElementById('editingPlayerName').value;
    const isEditing = editingName !== '';

    const name = document.getElementById('playerName').value.trim();
    const device = document.getElementById('audioDevice').value;
    const serverUrl = document.getElementById('serverUrl').value.trim();
    const volume = parseInt(document.getElementById('initialVolume').value);

    if (!name) {
        showAlert('Please enter a player name', 'warning');
        return;
    }

    if (!device) {
        showAlert('Please select an audio device', 'warning');
        return;
    }

    // Disable submit button to prevent double-click
    const submitBtn = document.getElementById('playerModalSubmit');
    submitBtn.disabled = true;

    try {
        if (isEditing) {
            // Edit mode: Use PUT to update config, then restart if needed
            const updatePayload = {
                name: name !== editingName ? name : undefined,  // Only include if changed
                device: device,  // Device is required
                serverUrl: serverUrl || ''  // Empty string = mDNS discovery
                // Note: volume is NOT included here - we update startup volume separately
                // so it doesn't affect current playback
            };

            // Include advertised format if advanced formats enabled
            if (advancedFormatsEnabled) {
                const form = document.getElementById('playerForm');
                const originalFormat = form.dataset.originalFormat || 'flac-48000';
                const currentFormat = document.getElementById('advertisedFormat').value || 'flac-48000';

                // Only include if changed from original
                if (currentFormat !== originalFormat) {
                    // Send the specific format
                    updatePayload.advertisedFormat = currentFormat;
                }
            }

            const response = await fetch(`./api/players/${encodeURIComponent(editingName)}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(updatePayload)
            });

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.error || 'Failed to update player');
            }

            const result = await response.json();
            const finalName = result.playerName || name;
            const wasRenamed = name !== editingName;

            // Update startup volume separately (doesn't affect current playback)
            const startupVolumeResponse = await fetch(`./api/players/${encodeURIComponent(finalName)}/startup-volume`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ volume })
            });
            if (!startupVolumeResponse.ok) {
                console.warn('Failed to update startup volume');
            }

            // Update auto-resume setting
            const autoResume = document.getElementById('autoResume').checked;
            const autoResumeResponse = await fetch(`./api/players/${encodeURIComponent(finalName)}/auto-resume`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled: autoResume })
            });
            if (!autoResumeResponse.ok) {
                console.warn('Failed to update auto-resume setting');
            }

            // Close modal and reset form
            bootstrap.Modal.getInstance(document.getElementById('playerModal')).hide();
            document.getElementById('playerForm').reset();
            document.getElementById('initialVolumeValue').textContent = '75%';

            // Show appropriate message based on changes
            if (result.needsRestart) {
                if (wasRenamed) {
                    // For renames, offer to restart rather than auto-restart
                    // The name change is saved locally, but Music Assistant needs a restart to see it
                    await refreshStatus(true);
                    showAlert(
                        `Player renamed to "${finalName}". Restart the player for the name to appear in Music Assistant.`,
                        'info',
                        8000 // Show for longer since it's actionable
                    );
                } else {
                    // For other changes requiring restart (e.g., server URL, format), auto-restart
                    const restartResponse = await fetch(`./api/players/${encodeURIComponent(finalName)}/restart`, {
                        method: 'POST'
                    });
                    if (!restartResponse.ok) {
                        console.warn('Restart request failed, player may need manual restart');
                    }
                    // Refresh status AFTER restart completes
                    await refreshStatus(true);
                    showAlert(`Player "${finalName}" updated and restarted`, 'success');
                }
            } else {
                await refreshStatus(true);
                showAlert(`Player "${finalName}" updated successfully`, 'success');
            }
        } else {
            // Add mode: Create new player
            const payload = {
                name,
                device,  // Device is required
                serverUrl: serverUrl || null,
                volume,
                persist: true
            };

            // Include advertised format if advanced formats enabled
            if (advancedFormatsEnabled) {
                const advertisedFormat = document.getElementById('advertisedFormat').value;
                if (advertisedFormat) {
                    payload.advertisedFormat = advertisedFormat;
                }
            }

            const response = await fetch('./api/players', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.error || 'Failed to create player');
            }

            // Update auto-resume setting for the new player
            const autoResume = document.getElementById('autoResume').checked;
            if (autoResume) {
                // Only call API if enabled (default is false)
                const autoResumeResponse = await fetch(`./api/players/${encodeURIComponent(name)}/auto-resume`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ enabled: autoResume })
                });
                if (!autoResumeResponse.ok) {
                    console.warn('Failed to update auto-resume setting');
                }
            }

            // Close modal and refresh
            bootstrap.Modal.getInstance(document.getElementById('playerModal')).hide();
            document.getElementById('playerForm').reset();
            document.getElementById('initialVolumeValue').textContent = '75%';
            await refreshStatus(true);

            showAlert(`Player "${name}" created successfully`, 'success');
        }
    } catch (error) {
        console.error('Error saving player:', error);
        showAlert(error.message, 'danger');
    } finally {
        submitBtn.disabled = false;
    }
}

async function deletePlayer(name) {
    if (!await showConfirm('Delete Player', `Delete player "${name}"? This will also remove its configuration.`, 'Delete', 'btn-danger')) {
        return;
    }

    try {
        const response = await fetch(`./api/players/${encodeURIComponent(name)}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || 'Failed to delete player');
        }

        await refreshStatus();
        showAlert(`Player "${name}" deleted`, 'success');
    } catch (error) {
        console.error('Error deleting player:', error);
        showAlert(error.message, 'danger');
    }
}

async function stopPlayer(name) {
    if (!await showConfirm('Stop Player', `Stop player "${name}"? This will disconnect it from the server.`, 'Stop', 'btn-warning')) {
        return;
    }

    try {
        const response = await fetch(`./api/players/${encodeURIComponent(name)}/stop`, {
            method: 'POST'
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || 'Failed to stop player');
        }

        await refreshStatus();
        showAlert(`Player "${name}" stopped`, 'info');
    } catch (error) {
        console.error('Error stopping player:', error);
        showAlert(error.message, 'danger');
    }
}

async function restartPlayer(name) {
    try {
        const response = await fetch(`./api/players/${encodeURIComponent(name)}/restart`, {
            method: 'POST'
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || 'Failed to restart player');
        }

        await refreshStatus();
        showAlert(`Player "${name}" restarted`, 'success');
    } catch (error) {
        console.error('Error restarting player:', error);
        showAlert(error.message, 'danger');
    }
}

async function setVolume(name, volume) {
    try {
        const response = await fetch(`./api/players/${encodeURIComponent(name)}/volume`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ volume: parseInt(volume) })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || 'Failed to set volume');
        }

        // Update local state
        if (players[name]) {
            players[name].volume = volume;
        }
        // Also update in pendingUpdate so other player updates aren't lost
        if (pendingUpdate?.players?.[name]) {
            pendingUpdate.players[name] = players[name];
        }
    } catch (error) {
        console.error('Error setting volume:', error);
        showAlert(error.message, 'danger');
    }
}

/**
 * Get the display state for a player's mute button.
 * Returns icon, class, and label based on mute state.
 */
function getPlayerMuteDisplayState(player) {
    const isMuted = player?.isMuted || false;
    return {
        isMuted,
        label: isMuted ? 'Muted' : 'Unmuted',
        icon: isMuted ? 'fa-volume-mute' : 'fa-volume-up',
        iconClass: isMuted ? 'text-danger' : 'text-success'
    };
}

/**
 * Toggle the mute state for a player.
 */
function togglePlayerMute(playerName) {
    const player = players[playerName];
    const isMuted = player?.isMuted || false;
    return setPlayerMute(playerName, !isMuted);
}

/**
 * Set the mute state for a player.
 * This is software mute on the audio pipeline (not hardware sink).
 * Syncs bidirectionally with Music Assistant.
 */
async function setPlayerMute(playerName, muted) {
    // Find and disable the button during the API call
    const card = document.querySelector(`.player-card[data-player="${CSS.escape(playerName)}"]`);
    const button = card?.querySelector('.card-mute-toggle');
    if (button) button.disabled = true;

    try {
        const response = await fetch(`./api/players/${encodeURIComponent(playerName)}/mute`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ muted })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to set mute state');
        }

        // Update local state
        if (players[playerName]) {
            players[playerName].isMuted = muted;
        }
        // Also update in pendingUpdate so other player updates aren't lost
        if (pendingUpdate?.players?.[playerName]) {
            pendingUpdate.players[playerName] = players[playerName];
        }

        // Update button UI
        if (button) {
            const state = getPlayerMuteDisplayState({ isMuted: muted });
            const icon = button.querySelector('i');
            if (icon) {
                icon.className = `fas ${state.icon} ${state.iconClass}`;
            }
            button.setAttribute('aria-label', state.label);
            button.setAttribute('title', state.label);
        }
    } catch (error) {
        console.error('Error setting mute state:', error);
        showAlert(error.message, 'danger');
    } finally {
        if (button) button.disabled = false;
    }
}


async function setDelay(name, delayMs) {
    // Clamp value to valid range
    delayMs = Math.max(-5000, Math.min(5000, parseInt(delayMs) || 0));

    // Update input field to show clamped value
    const input = document.getElementById('delayInput');
    if (input) input.value = delayMs;

    try {
        const response = await fetch(`./api/players/${encodeURIComponent(name)}/offset`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ delayMs: delayMs })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.error || 'Failed to set delay');
        }

        // Update local state
        if (players[name]) {
            players[name].delayMs = delayMs;
        }

        // Show "Saved" indicator briefly
        const indicator = document.getElementById('delaySavedIndicator');
        if (indicator) {
            indicator.style.opacity = '1';
            setTimeout(() => { indicator.style.opacity = '0'; }, 1500);
        }
    } catch (error) {
        console.error('Error setting delay:', error);
        showAlert(error.message, 'danger');
    }
}

function adjustDelay(name, delta) {
    const input = document.getElementById('delayInput');
    if (!input) return;

    const currentValue = parseInt(input.value) || 0;
    const newValue = Math.max(-5000, Math.min(5000, currentValue + delta));
    input.value = newValue;
    setDelay(name, newValue);
}

// Track delay changes for restart on modal close
let playerStatsInitialDelay = null;
let playerStatsCurrentPlayer = null;

async function handlePlayerStatsModalClose() {
    if (playerStatsCurrentPlayer && playerStatsInitialDelay !== null) {
        const player = players[playerStatsCurrentPlayer];
        if (player && player.delayMs !== playerStatsInitialDelay) {
            // Delay was changed, restart player to apply
            console.log(`Delay offset changed from ${playerStatsInitialDelay}ms to ${player.delayMs}ms, restarting player`);
            await restartPlayer(playerStatsCurrentPlayer);
        }
    }
    playerStatsInitialDelay = null;
    playerStatsCurrentPlayer = null;
}

async function showPlayerStats(name) {
    const player = players[name];
    if (!player) return;

    // Store initial delay to detect changes
    playerStatsInitialDelay = player.delayMs;
    playerStatsCurrentPlayer = name;

    const modal = document.getElementById('playerStatsModal');
    const body = document.getElementById('playerStatsBody');

    // Set up modal close handler to restart player if delay changed
    const modalInstance = bootstrap.Modal.getOrCreateInstance(modal);
    modal.removeEventListener('hidden.bs.modal', handlePlayerStatsModalClose);
    modal.addEventListener('hidden.bs.modal', handlePlayerStatsModalClose);

    // Determine Device vs Sink label based on sinkType
    const device = devices.find(d => d.id === player.device);
    const isCustomSink = device?.sinkType != null;
    const deviceLabel = isCustomSink ? 'Sink' : 'Device';

    // Format device/sink display value - show ID with alias/description if available
    let deviceDisplayValue;
    if (!player.device) {
        deviceDisplayValue = 'Default';
    } else if (device) {
        if (isCustomSink) {
            // Custom sink: show "sinkName (description)" or just "sinkName"
            deviceDisplayValue = device.alias ? `${device.name} (${device.alias})` : device.name;
        } else {
            // Hardware device: show "description (alias)" or just "description"
            deviceDisplayValue = device.alias ? `${device.name} (${device.alias})` : device.name;
        }
    } else {
        deviceDisplayValue = player.device;  // Fallback to raw ID
    }

    // Server display - three fields
    const serverName = player.serverName || '—';
    const serverAddress = player.connectedAddress || '—';
    const discoveryMethod = player.serverUrl ? 'Manual' : 'Auto-discovered';

    // Advertised format display
    const advertised = player.advertisedFormat || 'flac-48000';
    const isAllFormats = advertised === 'all';
    const advertisedDisplay = isAllFormats ? 'All Formats' : advertised;
    const advertisedSubtitle = isAllFormats ? '<br><small class="text-muted">flac • pcm • opus, up to 192kHz</small>' : '';

    // Output format - use device already looked up above
    const outputFormat = device
        ? `${formatSampleRate(device.defaultSampleRate)} ${device.bitDepth || 32}-bit float`
        : 'Unknown';

    // Check if player is playing to show receiving format
    const isPlaying = player.state === 'Playing' || player.state === 'Buffering';

    body.innerHTML = `
        <div class="row">
            <div class="col-md-6">
                <h6 class="text-muted text-uppercase small">Configuration</h6>
                <table class="table table-sm">
                    <tr><td><strong>Name</strong></td><td>${escapeHtml(player.name)}</td></tr>
                    <tr><td><strong>${deviceLabel}</strong></td><td>${escapeHtml(deviceDisplayValue)}</td></tr>
                    <tr><td><strong>Server</strong></td><td>${escapeHtml(serverName)}</td></tr>
                    <tr><td><strong>Address</strong></td><td>${escapeHtml(serverAddress)}</td></tr>
                    <tr><td><strong>Discovery</strong></td><td>${discoveryMethod}</td></tr>
                    <tr><td><strong>Advertised</strong></td><td>${escapeHtml(advertisedDisplay)}${advertisedSubtitle}</td></tr>
                    ${isPlaying ? `<tr><td><strong>Receiving</strong></td><td id="receivingFormat"><span class="text-muted">Loading...</span></td></tr>` : ''}
                    <tr><td><strong>Output</strong></td><td>${escapeHtml(outputFormat)}</td></tr>
                </table>
            </div>
            <div class="col-md-6">
                <h6 class="text-muted text-uppercase small">Status</h6>
                <table class="table table-sm">
                    <tr><td><strong>State</strong></td><td><span class="badge bg-${getStateBadgeClass(player.state)}">${getStateDisplayName(player)}</span></td></tr>
                    <tr><td><strong>Clock Synced</strong></td><td>${player.isClockSynced ? '<i class="fas fa-check text-success"></i> Yes' : '— No'}</td></tr>
                    <tr><td><strong>Muted</strong></td><td>${player.isMuted ? 'Yes' : 'No'}</td></tr>
                    <tr><td><strong>Latency</strong></td><td>${player.outputLatencyMs}ms</td></tr>
                    <tr><td><strong>Created</strong></td><td>${formatShortDateTime(player.createdAt)}</td></tr>
                    ${player.connectedAt ? `<tr><td><strong>Connected</strong></td><td>${formatShortDateTime(player.connectedAt)}</td></tr>` : ''}
                    ${player.errorMessage ? `<tr><td><strong>Error</strong></td><td class="text-danger">${escapeHtml(player.errorMessage)}</td></tr>` : ''}
                </table>
            </div>
        </div>
        ${player.currentTrack?.title ? `
        <div class="now-playing-section mt-3 p-3 bg-dark rounded">
            <h6 class="text-muted text-uppercase small mb-2"><i class="fas fa-music me-1"></i>Now Playing</h6>
            <div class="d-flex align-items-center">
                ${player.currentTrack.artworkUrl ? `<img src="${escapeHtml(player.currentTrack.artworkUrl)}" class="rounded me-3" style="width: 60px; height: 60px; object-fit: cover;" alt="Album art">` : ''}
                <div>
                    <div class="fw-semibold">${escapeHtml(player.currentTrack.title)}</div>
                    ${player.currentTrack.artist ? `<div class="text-muted small">${escapeHtml(player.currentTrack.artist)}</div>` : ''}
                    ${player.currentTrack.album ? `<div class="text-muted small">${escapeHtml(player.currentTrack.album)}</div>` : ''}
                </div>
            </div>
        </div>
        ` : ''}
        <h6 class="text-muted text-uppercase small mt-3">Delay Offset</h6>
        <p class="text-muted small mb-2">
            <i class="fas fa-info-circle me-1"></i>
            Adjust timing to sync with others. Changes apply immediately.
        </p>
        <div class="delay-control d-flex align-items-center gap-2 flex-wrap">
            <button class="btn btn-outline-secondary btn-sm" onclick="adjustDelay('${escapeJsString(name)}', -10)" title="Decrease by 10ms">
                <i class="fas fa-minus"></i>
            </button>
            <div class="input-group input-group-sm" style="max-width: 140px;">
                <input type="number" class="form-control text-center" id="delayInput"
                    value="${player.delayMs}" min="-5000" max="5000" step="10"
                    onchange="setDelay('${escapeJsString(name)}', this.value)"
                    onkeydown="if(event.key==='Enter'){setDelay('${escapeJsString(name)}', this.value); event.preventDefault();}">
                <span class="input-group-text">ms</span>
            </div>
            <button class="btn btn-outline-secondary btn-sm" onclick="adjustDelay('${escapeJsString(name)}', 10)" title="Increase by 10ms">
                <i class="fas fa-plus"></i>
            </button>
            <small class="text-muted">Range: ±5000ms</small>
            <span id="delaySavedIndicator" class="text-success small" style="opacity: 0; transition: opacity 0.3s;"><i class="fas fa-check"></i> Saved</span>
        </div>
    `;

    bootstrap.Modal.getOrCreateInstance(modal).show();

    // Fetch receiving format if player is playing (one-time fetch, not in audio hot path)
    if (isPlaying) {
        try {
            const response = await fetch(`./api/players/${encodeURIComponent(name)}/stats`);
            if (response.ok) {
                const stats = await response.json();
                const receivingEl = document.getElementById('receivingFormat');
                if (receivingEl && stats.audioFormat) {
                    const fmt = stats.audioFormat;
                    // Format: "FLAC 48kHz 1411kbps"
                    const codec = fmt.inputFormat || 'Unknown';
                    const sampleRate = fmt.inputSampleRate ? formatSampleRate(fmt.inputSampleRate) : '';
                    const bitrate = fmt.inputBitrate || '';
                    receivingEl.textContent = [codec, sampleRate, bitrate].filter(Boolean).join(' ');
                } else if (receivingEl) {
                    receivingEl.textContent = '—';
                }
            }
        } catch (e) {
            const receivingEl = document.getElementById('receivingFormat');
            if (receivingEl) receivingEl.textContent = '—';
        }
    }
}

// Render players
function renderPlayers() {

    const container = document.getElementById('players-container');
    const playerNames = Object.keys(players);

    if (playerNames.length === 0) {
        container.innerHTML = `
            <div class="col-12">
                <div class="empty-state">
                    <i class="fas fa-volume-mute"></i>
                    <h4>No Players Configured</h4>
                    <p>Click "Add Player" to create your first audio player.</p>
                </div>
            </div>
        `;
        return;
    }

    container.innerHTML = playerNames.map(name => {
        const player = players[name];
        const stateClass = getStateClass(player.state);
        const stateBadgeClass = getStateBadgeClass(player.state);

        return `
            <div class="col-md-6 col-lg-4 mb-3">
                <div class="card player-card h-100" data-player="${escapeHtml(name)}">
                    <div class="card-body">
                        <div class="d-flex justify-content-between align-items-start mb-2">
                            <h5 class="card-title mb-0">${escapeHtml(player.name)}</h5>
                            <div class="d-flex">
                                <button class="btn btn-sm btn-outline-info me-1" onclick="showPlayerStats('${escapeJsString(name)}')" title="Player Details">
                                    <i class="fas fa-info-circle"></i>
                                </button>
                                <button class="btn btn-sm btn-outline-secondary me-1" onclick="openStatsForNerds('${escapeJsString(name)}')" title="Stats for Nerds">
                                    <i class="fas fa-terminal"></i>
                                </button>
                                <div class="dropdown">
                                    <button class="btn btn-sm btn-outline-secondary" type="button" data-bs-toggle="dropdown">
                                        <i class="fas fa-ellipsis-v"></i>
                                    </button>
                                    <ul class="dropdown-menu">
                                        <li><a class="dropdown-item" href="#" onclick="openEditPlayerModal('${escapeJsString(name)}'); return false;"><i class="fas fa-edit me-2"></i>Edit</a></li>
                                        <li><a class="dropdown-item" href="#" onclick="restartPlayer('${escapeJsString(name)}'); return false;"><i class="fas fa-sync me-2"></i>Restart</a></li>
                                        <li><a class="dropdown-item" href="#" onclick="stopPlayer('${escapeJsString(name)}'); return false;"><i class="fas fa-stop me-2"></i>Stop</a></li>
                                        <li><hr class="dropdown-divider"></li>
                                        <li><a class="dropdown-item text-danger" href="#" onclick="deletePlayer('${escapeJsString(name)}'); return false;"><i class="fas fa-trash me-2"></i>Delete</a></li>
                                    </ul>
                                </div>
                            </div>
                        </div>

                        <div class="status-container mb-3">
                            <span class="status-indicator ${stateClass}"></span>
                            <span class="badge bg-${stateBadgeClass}">${getStateDisplayName(player)}</span>
                        </div>
                        <div class="connection-info text-muted small mb-2"
                             style="overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                             title="${escapeHtml(getConnectionInfo(player.device))}">
                            ${escapeHtml(getConnectionInfo(player.device))}
                        </div>

                        <div class="volume-control mb-3 pt-2 border-top">
                            <div class="d-flex align-items-center mb-1">
                                <i class="fas fa-volume-up me-1"></i>
                                <span class="small fw-semibold">Current Volume</span>
                                <i class="fas fa-info-circle ms-1 text-muted small volume-tooltip"
                                   data-bs-toggle="tooltip"
                                   data-bs-placement="top"
                                   data-bs-title="Current playback volume (syncs with Music Assistant)"></i>
                            </div>
                            <div class="d-flex align-items-center">
                                <input type="range" class="form-range form-range-sm flex-grow-1 volume-slider" min="0" max="100" value="${player.volume}"
                                    onchange="setVolume('${escapeJsString(name)}', this.value)"
                                    oninput="this.nextElementSibling.textContent = this.value + '%'; setVolumeDebounced('${escapeJsString(name)}', this.value)">
                                <span class="volume-display ms-2 small">${player.volume}%</span>
                                <button class="btn card-mute-toggle ms-2"
                                        title="${getPlayerMuteDisplayState(player).label}"
                                        aria-label="${getPlayerMuteDisplayState(player).label}"
                                        onclick="togglePlayerMute('${escapeJsString(name)}')">
                                    <i class="fas ${getPlayerMuteDisplayState(player).icon} ${getPlayerMuteDisplayState(player).iconClass}"></i>
                                </button>
                            </div>
                        </div>

                        <div class="player-status-area">
                            ${player.isClockSynced ? `
                                <small class="text-success"><i class="fas fa-clock me-1"></i>Clock synced</small>
                            ` : ''}
                            ${player.errorMessage ? `
                                <small class="text-danger d-block mt-1"><i class="fas fa-exclamation-circle me-1"></i>${escapeHtml(player.errorMessage)}</small>
                            ` : ''}
                        </div>
                    </div>
                </div>
            </div>
        `;
    }).join('');

    // Initialize Bootstrap tooltips for the rendered elements
    initializeTooltips();

    // Attach interaction tracking to all volume sliders
    attachSliderInteractionHandlers();
}

/**
 * Attaches mousedown/mouseup/touchstart/touchend handlers to volume sliders
 * to track user interaction and prevent DOM updates during drag.
 */
function attachSliderInteractionHandlers() {
    const sliders = document.querySelectorAll('.volume-slider');

    sliders.forEach(slider => {
        // Mouse events
        slider.addEventListener('mousedown', () => {
            isUserInteracting = true;
            console.log('Slider interaction started (mouse)');
        });

        // Touch events
        slider.addEventListener('touchstart', () => {
            isUserInteracting = true;
            console.log('Slider interaction started (touch)');
        });
    });

    // Global handlers for mouse/touch release
    // These fire when user releases anywhere, not just on the slider
    const handleInteractionEnd = () => {
        if (isUserInteracting) {
            isUserInteracting = false;
            console.log('Slider interaction ended');

            // Apply any pending updates
            if (pendingUpdate) {
                console.log('Applying pending update');
                players = pendingUpdate.players;
                pendingUpdate = null;
                renderPlayers();
            }
        }
    };

    // Use 'once: false' and remove old listeners to avoid duplicates
    document.removeEventListener('mouseup', handleInteractionEnd);
    document.removeEventListener('touchend', handleInteractionEnd);
    document.addEventListener('mouseup', handleInteractionEnd);
    document.addEventListener('touchend', handleInteractionEnd);
}

/**
 * Disposes all existing Bootstrap tooltips to prevent memory leaks and conflicts.
 * Also removes any orphaned tooltip popover elements from the DOM.
 */
function disposeTooltips() {
    const tooltipElements = document.querySelectorAll('[data-bs-toggle="tooltip"]');
    tooltipElements.forEach(element => {
        const tooltip = bootstrap.Tooltip.getInstance(element);
        if (tooltip) {
            tooltip.dispose();
        }
    });

    // Aggressively remove any orphaned tooltip popovers from the DOM
    // These can be left behind when tooltips are disposed while visible
    const orphanedTooltips = document.querySelectorAll('.tooltip');
    orphanedTooltips.forEach(tooltipElement => {
        tooltipElement.remove();
    });
}

/**
 * Initializes Bootstrap tooltips for elements with data-bs-toggle="tooltip"
 */
function initializeTooltips() {
    // First dispose any existing tooltips to prevent conflicts
    disposeTooltips();

    // Then create new tooltip instances
    const tooltipTriggerList = document.querySelectorAll('[data-bs-toggle="tooltip"]');
    tooltipTriggerList.forEach(tooltipTriggerEl => {
        new bootstrap.Tooltip(tooltipTriggerEl);
    });
}

function getStateClass(state) {
    const stateMap = {
        'Playing': 'status-playing',
        'Connected': 'status-connected',
        'Buffering': 'status-buffering',
        'Starting': 'status-starting',
        'Connecting': 'status-connecting',
        'Stopped': 'status-stopped',
        'Error': 'status-error',
        'Reconnecting': 'status-connecting',
        'WaitingForServer': 'status-connecting'
    };
    return stateMap[state] || 'status-stopped';
}

function getStateBadgeClass(state) {
    const stateMap = {
        'Playing': 'success',
        'Connected': 'success',
        'Buffering': 'warning',
        'Starting': 'info',
        'Connecting': 'info',
        'Created': 'secondary',
        'Stopped': 'secondary',
        'Error': 'danger',
        'Reconnecting': 'warning',
        'WaitingForServer': 'info'
    };
    return stateMap[state] || 'secondary';
}

function getStateDisplayName(player) {
    if (player.state === 'WaitingForServer') return 'Waiting for mDNS discovery';
    if (player.state === 'Reconnecting' && player.reconnectionAttempts) {
        return `Reconnecting (attempt ${player.reconnectionAttempts})`;
    }
    return player.state;
}

// Alert helpers
/**
 * Show a toast notification
 * @param {string} message - The message to display
 * @param {string} type - 'success', 'danger', 'warning', or 'info'
 * @param {number} duration - Auto-hide delay in milliseconds (0 = no auto-hide)
 */
function showAlert(message, type = 'info', duration = 5000) {
    const container = document.getElementById('toast-container');

    // Create toast element
    const toastEl = document.createElement('div');
    toastEl.className = 'toast';
    toastEl.setAttribute('role', 'alert');
    toastEl.setAttribute('aria-live', 'polite');
    toastEl.setAttribute('aria-atomic', 'true');

    // Icon mapping
    const typeConfig = {
        success: { icon: 'fa-check-circle', color: 'text-success', title: 'Success' },
        danger: { icon: 'fa-exclamation-circle', color: 'text-danger', title: 'Error' },
        warning: { icon: 'fa-exclamation-triangle', color: 'text-warning', title: 'Warning' },
        info: { icon: 'fa-info-circle', color: 'text-info', title: 'Info' }
    };

    const config = typeConfig[type] || typeConfig.info;

    toastEl.innerHTML = `
        <div class="toast-header">
            <i class="fas ${config.icon} ${config.color} me-2"></i>
            <strong class="me-auto">${config.title}</strong>
            <button type="button" class="btn-close" data-bs-dismiss="toast" aria-label="Close"></button>
        </div>
        <div class="toast-body">
            ${escapeHtml(message)}
        </div>
    `;

    container.appendChild(toastEl);

    // Initialize Bootstrap toast
    const toast = new bootstrap.Toast(toastEl, {
        autohide: duration > 0,
        delay: duration
    });

    toast.show();

    // Remove from DOM after hidden
    toastEl.addEventListener('hidden.bs.toast', () => {
        toastEl.remove();
    });
}

// ========== Stats for Nerds ==========
let statsInterval = null;
let currentStatsPlayer = null;
let isStatsFetching = false;
let cachedHardwareInfo = null; // Cache hardware format info (static during playback)
let statsPanelInitialized = false; // Track if panel structure has been created

function openStatsForNerds(playerName) {
    // Clear any existing interval first to prevent multiple polling loops
    if (statsInterval) {
        clearInterval(statsInterval);
        statsInterval = null;
    }

    currentStatsPlayer = playerName;
    cachedHardwareInfo = null; // Clear cache - will be populated on first fetch
    statsPanelInitialized = false; // Will rebuild panel structure on first render

    // Update player name in modal header
    const playerNameSpan = document.getElementById('statsPlayerName');
    if (playerNameSpan) {
        playerNameSpan.textContent = '• ' + playerName;
    }

    // Show modal
    const modal = new bootstrap.Modal(document.getElementById('statsForNerdsModal'));
    modal.show();

    // Start non-overlapping polling: waits for each request to complete
    // before scheduling the next, preventing request pileup under load.
    async function pollStats() {
        if (!currentStatsPlayer) return;
        await fetchAndRenderStats();
        if (currentStatsPlayer) {
            statsInterval = setTimeout(pollStats, 2000);
        }
    }
    pollStats();

    // Stop polling when modal closes
    document.getElementById('statsForNerdsModal').addEventListener('hidden.bs.modal', () => {
        clearTimeout(statsInterval);
        statsInterval = null;
        currentStatsPlayer = null;
    }, { once: true });
}

async function fetchAndRenderStats() {
    if (!currentStatsPlayer) return;
    if (isStatsFetching) return; // Prevent overlapping requests

    isStatsFetching = true;
    try {
        const response = await fetch(`./api/players/${encodeURIComponent(currentStatsPlayer)}/stats`);
        if (!response.ok) {
            throw new Error('Failed to fetch stats');
        }
        const stats = await response.json();

        // Cache hardware info on first fetch (static during playback)
        if (!cachedHardwareInfo && stats.audioFormat) {
            cachedHardwareInfo = {
                hardwareFormat: stats.audioFormat.hardwareFormat,
                hardwareSampleRate: stats.audioFormat.hardwareSampleRate,
                hardwareBitDepth: stats.audioFormat.hardwareBitDepth
            };
        }

        // Use cached hardware info for rendering
        if (cachedHardwareInfo && stats.audioFormat) {
            stats.audioFormat.hardwareFormat = cachedHardwareInfo.hardwareFormat;
            stats.audioFormat.hardwareSampleRate = cachedHardwareInfo.hardwareSampleRate;
            stats.audioFormat.hardwareBitDepth = cachedHardwareInfo.hardwareBitDepth;
        }

        renderStatsPanel(stats);
    } catch (error) {
        console.error('Error fetching stats:', error);
        const body = document.getElementById('statsForNerdsBody');
        body.innerHTML = `
            <div class="text-center py-4">
                <i class="fas fa-exclamation-triangle text-warning mb-2" style="font-size: 2rem;"></i>
                <p class="text-muted mb-0">Failed to load stats</p>
            </div>
        `;
    } finally {
        isStatsFetching = false;
    }
}

function renderStatsPanel(stats) {
    const body = document.getElementById('statsForNerdsBody');

    // First render: create the full structure with IDs
    // Note: innerHTML used here with static template strings (no user input) - safe
    if (!statsPanelInitialized) {
        body.innerHTML = `
            <!-- Hero Section: At-a-glance health indicator -->
            <div class="stats-hero" id="stats-hero">
                <div class="stats-hero-status">
                    <div class="stats-hero-indicator" id="stats-hero-indicator"></div>
                    <div class="stats-hero-text">
                        <span class="stats-hero-label" id="stats-hero-label">Checking...</span>
                        <span class="stats-hero-detail" id="stats-hero-detail"></span>
                    </div>
                </div>
                <div class="stats-hero-metrics">
                    <div class="stats-hero-metric">
                        <span class="stats-hero-metric-value" id="stats-hero-sync"></span>
                        <span class="stats-hero-metric-label">Sync</span>
                    </div>
                    <div class="stats-hero-metric">
                        <span class="stats-hero-metric-value" id="stats-hero-buffer"></span>
                        <span class="stats-hero-metric-label">Buffer</span>
                    </div>
                    <div class="stats-hero-metric">
                        <span class="stats-hero-metric-value" id="stats-hero-timing"></span>
                        <span class="stats-hero-metric-label">Timing</span>
                    </div>
                </div>
            </div>

            <!-- Identity Section (debug info moved from Player Details) -->
            <div class="stats-section">
                <div class="stats-section-header">Identity</div>
                <div class="stats-row">
                    <span class="stats-label">Client ID</span>
                    <span id="stats-client-id" class="stats-value"><code></code></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">SDK Version</span>
                    <span id="stats-sdk-version" class="stats-value info"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Server Time</span>
                    <span id="stats-server-time" class="stats-value"></span>
                </div>
            </div>

            <!-- Audio Format Section -->
            <div class="stats-section">
                <div class="stats-section-header">Audio Format</div>
                <div class="stats-row">
                    <span class="stats-label">Input</span>
                    <span id="stats-input-format" class="stats-value info"></span>
                </div>
                <div class="stats-row" id="stats-bitrate-row" style="display: none;">
                    <span class="stats-label">Bitrate</span>
                    <span id="stats-input-bitrate" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Output</span>
                    <span id="stats-output-format" class="stats-value info"></span>
                </div>
                <div class="stats-row" id="stats-hardware-row" style="display: none;">
                    <span class="stats-label">Hardware</span>
                    <span id="stats-hardware-format" class="stats-value info"></span>
                </div>
            </div>

            <!-- Sync Status Section -->
            <div class="stats-section">
                <div class="stats-section-header">Sync Status</div>
                <div class="stats-row">
                    <span class="stats-label">Sync Error</span>
                    <span id="stats-sync-error" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Status</span>
                    <span id="stats-sync-status" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Playback Active</span>
                    <span id="stats-playback-active" class="stats-value"></span>
                </div>
            </div>

            <!-- Buffer Section -->
            <div class="stats-section">
                <div class="stats-section-header">Buffer</div>
                <div class="stats-row">
                    <span class="stats-label">Buffered</span>
                    <span id="stats-buffered" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Underruns</span>
                    <span id="stats-underruns" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Overruns</span>
                    <span id="stats-overruns" class="stats-value"></span>
                </div>
            </div>

            <!-- Sync Correction Section -->
            <div class="stats-section">
                <div class="stats-section-header">Sync Correction</div>
                <div class="stats-row">
                    <span class="stats-label">Mode</span>
                    <span id="stats-correction-mode" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Threshold</span>
                    <span id="stats-threshold" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Frames Dropped</span>
                    <span id="stats-frames-dropped" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Frames Inserted</span>
                    <span id="stats-frames-inserted" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Dropped (Overflow)</span>
                    <span id="stats-dropped-overflow" class="stats-value"></span>
                </div>
            </div>

            <!-- Clock Sync Section -->
            <div class="stats-section">
                <div class="stats-section-header">Clock Sync</div>
                <div class="stats-row">
                    <span class="stats-label">Status</span>
                    <span class="stats-value">
                        <span class="sync-indicator">
                            <span id="stats-clock-dot" class="sync-dot"></span>
                            <span id="stats-clock-status"></span>
                        </span>
                    </span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Clock Offset</span>
                    <span id="stats-clock-offset" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Uncertainty</span>
                    <span id="stats-uncertainty" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Drift Rate</span>
                    <span id="stats-drift-rate" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Measurements</span>
                    <span id="stats-measurements" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Output Latency</span>
                    <span id="stats-output-latency" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Static Delay</span>
                    <span id="stats-static-delay" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Timing Source</span>
                    <span id="stats-timing-source" class="stats-value"></span>
                </div>
            </div>

            <!-- Throughput Section -->
            <div class="stats-section">
                <div class="stats-section-header">Throughput</div>
                <div class="stats-row">
                    <span class="stats-label">Samples Written</span>
                    <span id="stats-samples-written" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Samples Read</span>
                    <span id="stats-samples-read" class="stats-value"></span>
                </div>
            </div>

            <!-- Buffer Diagnostics Section -->
            <div class="stats-section">
                <div class="stats-section-header">
                    <i class="fas fa-stethoscope me-1"></i>Buffer Diagnostics
                </div>
                <div class="stats-row">
                    <span class="stats-label">State</span>
                    <span id="stats-diag-state" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Fill Level</span>
                    <span id="stats-fill-level" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Pipeline State</span>
                    <span id="stats-pipeline-state" class="stats-value info"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Has Received Data</span>
                    <span id="stats-has-received" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Dropped (Overflow)</span>
                    <span id="stats-diag-dropped" class="stats-value"></span>
                </div>
                <div class="stats-row">
                    <span class="stats-label">Smoothed Sync Error</span>
                    <span id="stats-smoothed-sync" class="stats-value"></span>
                </div>
            </div>
        `;
        statsPanelInitialized = true;
    }

    // Update values only (no DOM structure changes)

    // Hero Section: At-a-glance health indicator
    updateHeroSection(stats);

    // Client ID from player object (not from stats API)
    const player = players[currentStatsPlayer];
    if (player) {
        const clientIdEl = document.getElementById('stats-client-id');
        if (clientIdEl) {
            clientIdEl.innerHTML = `<code>${escapeHtml(player.clientId)}</code>`;
        }
    }

    // SDK version and server time for debugging
    updateStatsValue('stats-sdk-version', stats.sdkVersion || 'unknown');
    updateStatsValue('stats-server-time', stats.serverTime || '--');

    updateStatsValue('stats-input-format', stats.audioFormat.inputFormat);

    // Bitrate row - show/hide and update
    const bitrateRow = document.getElementById('stats-bitrate-row');
    if (stats.audioFormat.inputBitrate) {
        bitrateRow.style.display = '';
        updateStatsValue('stats-input-bitrate', stats.audioFormat.inputBitrate);
    } else {
        bitrateRow.style.display = 'none';
    }

    updateStatsValue('stats-output-format', stats.audioFormat.outputFormat);

    // Hardware row - show/hide and update
    const hardwareRow = document.getElementById('stats-hardware-row');
    if (stats.audioFormat.hardwareFormat) {
        hardwareRow.style.display = '';
        updateStatsValue('stats-hardware-format',
            `${stats.audioFormat.hardwareFormat} ${stats.audioFormat.hardwareSampleRate}Hz ${stats.audioFormat.hardwareBitDepth}-bit`);
    } else {
        hardwareRow.style.display = 'none';
    }

    // Sync Status - use smoothed error (what actually drives corrections)
    const smoothedSyncErrorMs = stats.diagnostics.smoothedSyncErrorUs / 1000;
    updateStatsValueWithClass('stats-sync-error', formatMs(smoothedSyncErrorMs), getSyncErrorClass(smoothedSyncErrorMs));
    updateStatsValueWithClass('stats-sync-status',
        stats.sync.isWithinTolerance ? 'Within tolerance' : 'Correcting',
        stats.sync.isWithinTolerance ? 'good' : 'warning');
    updateStatsValueWithClass('stats-playback-active',
        stats.sync.isPlaybackActive ? 'Yes' : 'No',
        stats.sync.isPlaybackActive ? 'good' : 'muted');

    // Buffer
    updateStatsValue('stats-buffered', `${stats.buffer.bufferedMs}ms / ${stats.buffer.targetMs}ms`);
    updateStatsValueWithClass('stats-underruns', formatCount(stats.buffer.underruns),
        stats.buffer.underruns > 0 ? 'bad' : 'good');
    updateStatsValueWithClass('stats-overruns', formatCount(stats.buffer.overruns),
        stats.buffer.overruns > 0 ? 'warning' : 'good');

    // Sync Correction
    updateStatsValueWithClass('stats-correction-mode', stats.correction.mode, getCorrectionModeClass(stats.correction.mode));
    updateStatsValue('stats-threshold', `${stats.correction.thresholdMs}ms`);
    updateStatsValueWithClass('stats-frames-dropped', formatSampleCount(stats.correction.framesDropped),
        stats.correction.framesDropped > 0 ? 'warning' : '');
    updateStatsValueWithClass('stats-frames-inserted', formatSampleCount(stats.correction.framesInserted),
        stats.correction.framesInserted > 0 ? 'warning' : '');
    updateStatsValueWithClass('stats-dropped-overflow', formatSampleCount(stats.throughput.samplesDroppedOverflow),
        stats.throughput.samplesDroppedOverflow > 0 ? 'bad' : '');

    // Clock Sync
    const clockDot = document.getElementById('stats-clock-dot');
    const clockStatus = document.getElementById('stats-clock-status');
    if (clockDot && clockStatus) {
        clockDot.className = 'sync-dot ' + (stats.clockSync.isSynchronized ? '' :
            (stats.clockSync.measurementCount > 0 ? 'syncing' : 'not-synced'));
        clockStatus.className = stats.clockSync.isSynchronized ? 'good' : 'warning';
        clockStatus.textContent = stats.clockSync.isSynchronized ? 'Synchronized' :
            (stats.clockSync.measurementCount > 0 ? 'Syncing...' : 'Not synced');
    }
    updateStatsValue('stats-clock-offset', formatMs(stats.clockSync.clockOffsetMs));
    updateStatsValue('stats-uncertainty', formatMs(stats.clockSync.uncertaintyMs));
    updateStatsValueWithClass('stats-drift-rate',
        `${stats.clockSync.driftRatePpm.toFixed(1)} ppm ${stats.clockSync.isDriftReliable ? '' : '(unstable)'}`,
        stats.clockSync.isDriftReliable ? '' : 'muted');
    updateStatsValue('stats-measurements', formatCount(stats.clockSync.measurementCount));
    updateStatsValue('stats-output-latency', `${stats.clockSync.outputLatencyMs}ms`);
    updateStatsValue('stats-static-delay', `${stats.clockSync.staticDelayMs}ms`);
    updateStatsValueWithClass('stats-timing-source',
        getTimingSourceLabel(stats.clockSync.timingSource),
        getTimingSourceClass(stats.clockSync.timingSource));

    // Throughput
    updateStatsValue('stats-samples-written', formatSampleCount(stats.throughput.samplesWritten));
    updateStatsValue('stats-samples-read', formatSampleCount(stats.throughput.samplesRead));

    // Buffer Diagnostics
    updateStatsValueWithClass('stats-diag-state', stats.diagnostics.state, getBufferStateClass(stats.diagnostics.state));
    updateStatsValue('stats-fill-level', `${stats.diagnostics.fillPercent}%`);
    updateStatsValue('stats-pipeline-state', stats.diagnostics.pipelineState);
    updateStatsValueWithClass('stats-has-received',
        stats.diagnostics.hasReceivedSamples ? 'Yes' : 'No',
        stats.diagnostics.hasReceivedSamples ? 'good' : 'warning');
    updateStatsValueWithClass('stats-diag-dropped', formatSampleCount(stats.diagnostics.droppedOverflow),
        stats.diagnostics.droppedOverflow > 0 ? 'bad' : 'good');
    updateStatsValue('stats-smoothed-sync', formatUs(stats.diagnostics.smoothedSyncErrorUs));
}

// Helper to update a stats value by ID
function updateStatsValue(id, value) {
    const el = document.getElementById(id);
    if (el) el.textContent = value;
}

// Helper to update a stats value and its CSS class
function updateStatsValueWithClass(id, value, className) {
    const el = document.getElementById(id);
    if (el) {
        el.textContent = value;
        el.className = 'stats-value ' + className;
    }
}

// Stats helper functions
function formatMs(ms) {
    if (Math.abs(ms) < 0.01) return '0.00ms';
    return (ms >= 0 ? '+' : '') + ms.toFixed(2) + 'ms';
}

function formatCount(count) {
    return count.toLocaleString();
}

function formatSampleCount(count) {
    if (count >= 1e9) return (count / 1e9).toFixed(2) + 'B';
    if (count >= 1e6) return (count / 1e6).toFixed(2) + 'M';
    if (count >= 1e3) return (count / 1e3).toFixed(1) + 'K';
    return count.toString();
}

function getSyncErrorClass(errorMs) {
    const absError = Math.abs(errorMs);
    if (absError < 5) return 'good';
    if (absError < 20) return 'warning';
    return 'bad';
}

function getCorrectionModeClass(mode) {
    switch (mode) {
        case 'None': return 'good';
        case 'Dropping': return 'warning';
        case 'Inserting': return 'warning';
        default: return '';
    }
}

function getBufferStateClass(state) {
    if (state === 'Playing') return 'good';
    if (state === 'Empty') return 'muted';
    if (state.includes('Waiting') || state.includes('Buffered')) return 'warning';
    if (state.includes('Stalled') || state.includes('dropping')) return 'bad';
    return '';
}

function formatUs(us) {
    if (Math.abs(us) < 1000) return us.toFixed(0) + 'us';
    return (us / 1000).toFixed(2) + 'ms';
}

// Timing source helpers
function getTimingSourceLabel(source) {
    switch (source) {
        case 'audio-clock': return 'Audio Clock';
        case 'monotonic': return 'Monotonic';
        case 'wall-clock': return 'Wall Clock';
        default: return source || 'Unknown';
    }
}

function getTimingSourceClass(source) {
    switch (source) {
        case 'audio-clock': return 'good';      // Best - hardware timing
        case 'monotonic': return 'warning';     // Fallback - filtered system timer
        case 'wall-clock': return 'bad';        // Worst - unstable, VM-vulnerable
        default: return 'muted';
    }
}

// Hero section update logic
function updateHeroSection(stats) {
    const indicator = document.getElementById('stats-hero-indicator');
    const label = document.getElementById('stats-hero-label');
    const detail = document.getElementById('stats-hero-detail');
    const heroSync = document.getElementById('stats-hero-sync');
    const heroBuffer = document.getElementById('stats-hero-buffer');
    const heroTiming = document.getElementById('stats-hero-timing');

    if (!indicator || !label) return;

    // Determine overall health based on key metrics
    // Use smoothed sync error (what actually drives corrections)
    const smoothedSyncMs = stats.diagnostics.smoothedSyncErrorUs / 1000;
    const syncError = Math.abs(smoothedSyncMs);
    const isPlaying = stats.sync.isPlaybackActive;
    const isSynced = stats.clockSync.isSynchronized;
    const hasUnderruns = stats.buffer.underruns > 0;
    const hasOverflow = stats.throughput.samplesDroppedOverflow > 0;
    const correctionMode = stats.correction.mode;
    const timingSource = stats.clockSync.timingSource;

    let health = 'good';
    let healthLabel = 'Healthy';
    let healthDetail = '';

    if (!isPlaying) {
        health = 'idle';
        healthLabel = 'Idle';
        healthDetail = 'Not playing';
    } else if (!isSynced) {
        health = 'warning';
        healthLabel = 'Syncing';
        healthDetail = 'Clock synchronization in progress';
    } else if (syncError > 50) {
        health = 'bad';
        healthLabel = 'Out of Sync';
        healthDetail = `Sync error: ${formatMs(smoothedSyncMs)}`;
    } else if (hasOverflow) {
        health = 'bad';
        healthLabel = 'Overflow';
        healthDetail = 'Buffer overflow - samples dropped';
    } else if (hasUnderruns) {
        health = 'warning';
        healthLabel = 'Underruns';
        healthDetail = 'Audio underruns detected';
    } else if (correctionMode !== 'None') {
        health = 'warning';
        healthLabel = 'Correcting';
        healthDetail = correctionMode === 'Dropping' ? 'Dropping frames (behind)' : 'Inserting frames (ahead)';
    } else if (syncError > 15) {
        health = 'warning';
        healthLabel = 'Drifting';
        healthDetail = `Sync error: ${formatMs(smoothedSyncMs)}`;
    } else if (timingSource === 'wall-clock') {
        health = 'warning';
        healthLabel = 'Degraded';
        healthDetail = 'Using wall clock (VM timer issues)';
    }

    // Update hero indicator
    indicator.className = 'stats-hero-indicator ' + health;
    label.textContent = healthLabel;
    label.className = 'stats-hero-label ' + health;
    detail.textContent = healthDetail;

    // Update quick metrics
    if (heroSync) {
        heroSync.textContent = formatMs(smoothedSyncMs);
        heroSync.className = 'stats-hero-metric-value ' + getSyncErrorClass(smoothedSyncMs);
    }
    if (heroBuffer) {
        heroBuffer.textContent = `${stats.buffer.bufferedMs}ms`;
        heroBuffer.className = 'stats-hero-metric-value';
    }
    if (heroTiming) {
        heroTiming.textContent = getTimingSourceLabel(timingSource);
        heroTiming.className = 'stats-hero-metric-value ' + getTimingSourceClass(timingSource);
    }
}

// ============================================
// CUSTOM SINKS MANAGEMENT
// ============================================

// State
let customSinks = {};
let customSinksModal = null;

// Open custom sinks modal
function openCustomSinksModal() {
    if (!customSinksModal) {
        customSinksModal = new bootstrap.Modal(document.getElementById('customSinksModal'));
    }
    customSinksModal.show();
    refreshSinks();
}

// Refresh custom sinks list
async function refreshSinks() {
    const container = document.getElementById('customSinksContainer');
    if (!container) return;

    try {
        const response = await fetch('./api/sinks');
        if (!response.ok) throw new Error('Failed to fetch sinks');

        const data = await response.json();
        customSinks = {};
        (data.sinks || []).forEach(s => {
            customSinks[s.name] = s;
        });

        renderSinks();
    } catch (error) {
        console.error('Error refreshing sinks:', error);
        container.innerHTML = `<div class="text-center py-3 text-danger">
            <i class="fas fa-exclamation-circle me-2"></i>Failed to load sinks
        </div>`;
    }
}

// Render sink cards
function renderSinks() {
    const container = document.getElementById('customSinksContainer');
    if (!container) return;
    const sinkNames = Object.keys(customSinks);

    if (sinkNames.length === 0) {
        container.innerHTML = `
            <div class="text-center py-4">
                <i class="fas fa-layer-group fa-3x text-muted mb-3 d-block"></i>
                <h5>No Custom Sinks</h5>
                <p class="text-muted mb-0">Create a combine-sink or remap-sink to get started.</p>
            </div>
        `;
        return;
    }

    container.innerHTML = '<div class="row">' + sinkNames.map(name => {
        const sink = customSinks[name];
        const typeIcon = sink.type === 'Combine' ? 'fa-layer-group' : 'fa-random';
        const typeBadgeClass = sink.type === 'Combine' ? 'bg-info' : 'bg-secondary';
        const stateBadgeClass = getSinkStateBadgeClass(sink.state);
        const isLoaded = sink.state === 'Loaded';

        return `
            <div class="col-md-6 mb-3">
                <div class="card sink-card h-100">
                    <div class="card-body">
                        <div class="d-flex justify-content-between align-items-start mb-2">
                            <h6 class="card-title mb-0">
                                <i class="fas ${typeIcon} me-2 text-muted"></i>
                                ${escapeHtml(sink.name)}
                            </h6>
                            <div class="btn-group btn-group-sm">
                                <button class="btn btn-outline-primary"
                                        id="sink-test-btn-${escapeHtml(name)}"
                                        onclick="playTestToneForSink('${escapeJsString(name)}')"
                                        title="Play test tone"
                                        ${!isLoaded ? 'disabled' : ''}>
                                    <i class="fas fa-volume-up"></i>
                                </button>
                                <div class="dropdown">
                                    <button class="btn btn-outline-secondary" data-bs-toggle="dropdown">
                                        <i class="fas fa-ellipsis-v"></i>
                                    </button>
                                    <ul class="dropdown-menu dropdown-menu-end">
                                        <li><a class="dropdown-item" href="#" onclick="editSink('${escapeJsString(name)}'); return false;">
                                            <i class="fas fa-edit me-2"></i>Edit</a></li>
                                        <li><a class="dropdown-item" href="#" onclick="reloadSink('${escapeJsString(name)}'); return false;">
                                            <i class="fas fa-sync me-2"></i>Reload</a></li>
                                        <li><hr class="dropdown-divider"></li>
                                        <li><a class="dropdown-item text-danger" href="#" onclick="deleteSink('${escapeJsString(name)}'); return false;">
                                            <i class="fas fa-trash me-2"></i>Delete</a></li>
                                    </ul>
                                </div>
                            </div>
                        </div>

                        <span class="badge ${typeBadgeClass} sink-type-badge me-1">${sink.type}</span>
                        <span class="badge bg-${stateBadgeClass}">${sink.state}</span>

                        ${sink.description ? `<p class="text-muted mt-2 mb-0 small">${escapeHtml(sink.description)}</p>` : ''}

                        ${sink.slaves && sink.slaves.length > 0 ? `
                            <div class="mt-2">
                                <small class="text-muted d-block mb-1"><i class="fas fa-link me-1"></i>Combined devices:</small>
                                <div class="d-flex flex-wrap gap-1">
                                    ${sink.slaves.map(s => `<span class="sink-device-badge">${escapeHtml(getDeviceDisplayName(s))}</span>`).join('')}
                                </div>
                            </div>
                        ` : ''}

                        ${sink.masterSink ? `
                            <div class="mt-2">
                                <small class="text-muted"><i class="fas fa-arrow-right me-1"></i>From: ${escapeHtml(getDeviceDisplayName(sink.masterSink))}</small>
                            </div>
                        ` : ''}

                        ${sink.channelMap && sink.channelMap.length > 0 ? `
                            <div class="channel-mapping mt-2">
                                <small class="text-muted d-block mb-1"><i class="fas fa-random me-1"></i>Channel mapping:</small>
                                ${sink.channelMap.map(m => `
                                    <div class="channel-mapping-row">
                                        <span>${formatChannelName(m.outputChannel)}</span>
                                        <span class="text-muted">←</span>
                                        <span>${formatChannelName(m.sourceChannel)}</span>
                                    </div>
                                `).join('')}
                            </div>
                        ` : ''}

                        ${sink.errorMessage ? `
                            <div class="mt-2">
                                <small class="text-danger">
                                    <i class="fas fa-exclamation-circle me-1"></i>${escapeHtml(sink.errorMessage)}
                                </small>
                            </div>
                        ` : ''}
                    </div>
                </div>
            </div>
        `;
    }).join('') + '</div>';
}

function getSinkStateBadgeClass(state) {
    const stateMap = {
        'Loaded': 'success',
        'Loading': 'info',
        'Error': 'danger',
        'Created': 'secondary',
        'Unloading': 'warning'
    };
    return stateMap[state] || 'secondary';
}

// Track if we're editing an existing sink
let editingCombineSink = null;
let editingRemapSink = null;

// Cached modal instances to avoid creating duplicates
let combineSinkModalInstance = null;
let remapSinkModalInstance = null;
let importSinksModalInstance = null;

// Open Combine Sink Modal (editData is optional - if provided, we're editing)
function openCombineSinkModal(editData = null) {
    // Hide parent modal to avoid stacking issues
    if (customSinksModal) {
        customSinksModal.hide();
    }

    // Track if editing
    editingCombineSink = editData;

    // Update modal title based on mode
    const modalTitle = document.querySelector('#combineSinkModal .modal-title');
    if (modalTitle) {
        modalTitle.textContent = editData ? 'Edit Combine Sink' : 'Create Combine Sink';
    }

    // Update button text
    const createBtn = document.querySelector('#combineSinkModal .btn-primary');
    if (createBtn) {
        createBtn.textContent = editData ? 'Save Changes' : 'Create Sink';
    }

    // Fill form with existing data or reset
    const nameInput = document.getElementById('combineSinkName');
    nameInput.value = editData ? editData.name : '';
    nameInput.disabled = !!editData; // Disable name field when editing, enable for create
    document.getElementById('combineSinkDesc').value = editData?.description || '';

    // Populate device list (exclude hidden devices, but include current slaves even if hidden)
    const deviceList = document.getElementById('combineDeviceList');
    const selectedSlaves = editData?.slaves || [];
    const eligibleDevices = devices.filter(d => !d.hidden || selectedSlaves.includes(d.id));
    if (eligibleDevices.length === 0) {
        deviceList.innerHTML = '<div class="text-center py-2 text-muted">No devices available</div>';
    } else {
        deviceList.innerHTML = eligibleDevices.map(d => {
            const hiddenNote = d.hidden ? ' <span class="badge bg-secondary ms-1">hidden</span>' : '';
            return `
            <div class="form-check device-checkbox-item">
                <input class="form-check-input" type="checkbox" value="${escapeHtml(d.id)}" id="combine-${escapeHtml(d.id)}"
                    ${selectedSlaves.includes(d.id) ? 'checked' : ''}>
                <label class="form-check-label" for="combine-${escapeHtml(d.id)}">
                    ${escapeHtml(d.alias || d.name)}
                    ${d.isDefault ? '<span class="badge bg-primary ms-1">default</span>' : ''}${hiddenNote}
                </label>
            </div>
        `}).join('');
    }

    // Get or create modal instance (avoid creating duplicates)
    const modalEl = document.getElementById('combineSinkModal');
    if (!combineSinkModalInstance) {
        combineSinkModalInstance = new bootstrap.Modal(modalEl);
        // Set up the hidden event handler once
        modalEl.addEventListener('hidden.bs.modal', () => {
            editingCombineSink = null; // Clear edit state
            if (customSinksModal) {
                customSinksModal.show();
            }
        });
    }
    combineSinkModalInstance.show();
}

// Open Remap Sink Modal (editData is optional - if provided, we're editing)
function openRemapSinkModal(editData = null) {
    // Hide parent modal to avoid stacking issues
    if (customSinksModal) {
        customSinksModal.hide();
    }

    // Track if editing
    editingRemapSink = editData;

    // Update modal title based on mode
    const modalTitle = document.querySelector('#remapSinkModal .modal-title');
    if (modalTitle) {
        modalTitle.textContent = editData ? 'Edit Remap Sink' : 'Create Remap Sink';
    }

    // Update button text
    const createBtn = document.querySelector('#remapSinkModal .btn-primary');
    if (createBtn) {
        createBtn.textContent = editData ? 'Save Changes' : 'Create Sink';
    }

    // Fill form with existing data or reset
    const nameInput = document.getElementById('remapSinkName');
    nameInput.value = editData ? editData.name : '';
    nameInput.disabled = !!editData; // Disable name field when editing, enable for create
    document.getElementById('remapSinkDesc').value = editData?.description || '';

    // Populate master device dropdown:
    // - Exclude remap sinks (they can't be masters of other remap sinks)
    // - Exclude hidden devices (manually hidden via wizard)
    // - But when editing, include the current masterSink even if hidden (so edit always works)
    const masterSelect = document.getElementById('remapMasterDevice');
    const currentMaster = editData?.masterSink;
    const eligibleDevices = devices.filter(d => {
        if (d.sinkType === 'Remap') return false;
        if (d.hidden && d.id !== currentMaster) return false;
        return true;
    });
    masterSelect.innerHTML = '<option value="">Select a device...</option>' +
        eligibleDevices.map(d => {
            const hiddenNote = d.hidden ? ' (hidden)' : '';
            return `<option value="${escapeHtml(d.id)}">${escapeHtml(d.alias || d.name)} (${d.maxChannels}ch)${hiddenNote}</option>`;
        }).join('');

    // Set master device if editing
    if (currentMaster) {
        masterSelect.value = currentMaster;
    }

    // Determine output mode from channel mappings (mono if single 'mono' output channel)
    const isMono = editData?.channelMappings?.length === 1 &&
                   editData.channelMappings[0].outputChannel === 'mono';

    // Set output mode toggle
    document.getElementById('outputModeMono').checked = isMono;
    document.getElementById('outputModeStereo').checked = !isMono;

    updateChannelPicker();

    // Set channel mappings if editing
    if (editData?.channelMappings) {
        if (isMono) {
            const monoMapping = editData.channelMappings[0];
            if (monoMapping) {
                document.getElementById('monoChannel').value = monoMapping.masterChannel;
            }
        } else if (editData.channelMappings.length >= 2) {
            const leftMapping = editData.channelMappings.find(m => m.outputChannel === 'front-left');
            const rightMapping = editData.channelMappings.find(m => m.outputChannel === 'front-right');
            if (leftMapping) {
                document.getElementById('leftChannel').value = leftMapping.masterChannel;
            }
            if (rightMapping) {
                document.getElementById('rightChannel').value = rightMapping.masterChannel;
            }
        }
    }

    // Get or create modal instance (avoid creating duplicates)
    const modalEl = document.getElementById('remapSinkModal');
    if (!remapSinkModalInstance) {
        remapSinkModalInstance = new bootstrap.Modal(modalEl);
        // Set up the hidden event handler once
        modalEl.addEventListener('hidden.bs.modal', () => {
            editingRemapSink = null; // Clear edit state
            if (customSinksModal) {
                customSinksModal.show();
            }
        });
    }
    remapSinkModalInstance.show();
}

// Update channel picker based on selected master device and output mode
function updateChannelPicker() {
    const masterSelect = document.getElementById('remapMasterDevice');
    const channelPicker = document.getElementById('channelPicker');
    const isMono = document.getElementById('outputModeMono').checked;

    // Get channel count from selected device
    const selectedDevice = devices.find(d => d.id === masterSelect.value);
    const channelCount = selectedDevice ? selectedDevice.maxChannels : 2;

    // Build channel options based on channel count
    let channelOptions = [];
    if (channelCount >= 8) {
        channelOptions = [
            { value: 'front-left', label: 'Front Left' },
            { value: 'front-right', label: 'Front Right' },
            { value: 'front-center', label: 'Front Center' },
            { value: 'lfe', label: 'LFE (Subwoofer)' },
            { value: 'rear-left', label: 'Rear Left' },
            { value: 'rear-right', label: 'Rear Right' },
            { value: 'side-left', label: 'Side Left' },
            { value: 'side-right', label: 'Side Right' }
        ];
    } else if (channelCount >= 6) {
        channelOptions = [
            { value: 'front-left', label: 'Front Left' },
            { value: 'front-right', label: 'Front Right' },
            { value: 'front-center', label: 'Front Center' },
            { value: 'lfe', label: 'LFE (Subwoofer)' },
            { value: 'rear-left', label: 'Rear Left' },
            { value: 'rear-right', label: 'Rear Right' }
        ];
    } else {
        channelOptions = [
            { value: 'front-left', label: 'Front Left' },
            { value: 'front-right', label: 'Front Right' }
        ];
    }

    const optionsHtml = channelOptions.map(ch =>
        `<option value="${ch.value}">${ch.label}</option>`
    ).join('');

    if (isMono) {
        // Mono mode: single channel picker
        channelPicker.innerHTML = `
            <div class="channel-pair mb-2 d-flex align-items-center">
                <span class="output-label">Output</span>
                <i class="fas fa-arrow-left mx-2 text-muted"></i>
                <select class="form-select form-select-sm channel-select" id="monoChannel">
                    ${optionsHtml}
                </select>
                <button class="btn btn-outline-primary btn-sm ms-2"
                        id="monoChannelTestBtn"
                        onclick="playChannelTestTone('mono')"
                        title="Play test tone">
                    <i class="fas fa-volume-up"></i>
                </button>
            </div>
        `;
        document.getElementById('monoChannel').value = 'front-left';
    } else {
        // Stereo mode: left and right channel pickers
        channelPicker.innerHTML = `
            <div class="channel-pair mb-2 d-flex align-items-center">
                <span class="output-label">Left Output</span>
                <i class="fas fa-arrow-left mx-2 text-muted"></i>
                <select class="form-select form-select-sm channel-select" id="leftChannel">
                    ${optionsHtml}
                </select>
                <button class="btn btn-outline-primary btn-sm ms-2"
                        id="leftChannelTestBtn"
                        onclick="playChannelTestTone('left')"
                        title="Play test tone">
                    <i class="fas fa-volume-up"></i>
                </button>
            </div>
            <div class="channel-pair mb-2 d-flex align-items-center">
                <span class="output-label">Right Output</span>
                <i class="fas fa-arrow-left mx-2 text-muted"></i>
                <select class="form-select form-select-sm channel-select" id="rightChannel">
                    ${optionsHtml}
                </select>
                <button class="btn btn-outline-primary btn-sm ms-2"
                        id="rightChannelTestBtn"
                        onclick="playChannelTestTone('right')"
                        title="Play test tone">
                    <i class="fas fa-volume-up"></i>
                </button>
            </div>
        `;
        document.getElementById('leftChannel').value = 'front-left';
        document.getElementById('rightChannel').value = 'front-right';
    }
}

// Create or update combine sink
async function createCombineSink() {
    const name = document.getElementById('combineSinkName').value.trim();
    const description = document.getElementById('combineSinkDesc').value.trim();
    const checkboxes = document.querySelectorAll('#combineDeviceList input:checked');
    const slaves = Array.from(checkboxes).map(cb => cb.value);
    const isEditing = !!editingCombineSink;

    if (!name) {
        showAlert('Please enter a sink name', 'warning');
        return;
    }

    if (slaves.length < 2) {
        showAlert('Select at least 2 devices to combine', 'warning');
        return;
    }

    try {
        // If editing, delete the old sink first
        if (isEditing) {
            const deleteResponse = await fetch(`./api/sinks/${encodeURIComponent(name)}`, {
                method: 'DELETE'
            });
            if (!deleteResponse.ok) {
                const error = await deleteResponse.json();
                throw new Error(error.message || 'Failed to delete existing sink');
            }
        }

        // Create the sink (new or recreated with updates)
        const response = await fetch('./api/sinks/combine', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name, description: description || null, slaves })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to create sink');
        }

        if (combineSinkModalInstance) {
            combineSinkModalInstance.hide();
        }
        await refreshSinks();
        await refreshDevices(); // Custom sink should now appear in device list
        showAlert(`Combine sink "${name}" ${isEditing ? 'updated' : 'created'} successfully`, 'success');
    } catch (error) {
        showAlert(error.message, 'danger');
    }
}

// Create or update remap sink
async function createRemapSink() {
    const name = document.getElementById('remapSinkName').value.trim();
    const description = document.getElementById('remapSinkDesc').value.trim();
    const masterSink = document.getElementById('remapMasterDevice').value;
    const isMono = document.getElementById('outputModeMono').checked;
    const isEditing = !!editingRemapSink;

    if (!name) {
        showAlert('Please enter a sink name', 'warning');
        return;
    }

    if (!masterSink) {
        showAlert('Please select a master device', 'warning');
        return;
    }

    // Build channel mappings based on output mode
    let channelMappings;
    let channels;
    if (isMono) {
        const monoChannel = document.getElementById('monoChannel').value;
        channelMappings = [
            { outputChannel: 'mono', masterChannel: monoChannel }
        ];
        channels = 1;
    } else {
        const leftChannel = document.getElementById('leftChannel').value;
        const rightChannel = document.getElementById('rightChannel').value;
        channelMappings = [
            { outputChannel: 'front-left', masterChannel: leftChannel },
            { outputChannel: 'front-right', masterChannel: rightChannel }
        ];
        channels = 2;
    }

    try {
        // If editing, delete the old sink first
        if (isEditing) {
            const deleteResponse = await fetch(`./api/sinks/${encodeURIComponent(name)}`, {
                method: 'DELETE'
            });
            if (!deleteResponse.ok) {
                const error = await deleteResponse.json();
                throw new Error(error.message || 'Failed to delete existing sink');
            }
        }

        // Create the sink (new or recreated with updates)
        // remix: true for mono (downmix stereo L+R to mono), false for stereo (1:1 mapping)
        const response = await fetch('./api/sinks/remap', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                name,
                description: description || null,
                masterSink,
                channels,
                channelMappings,
                remix: isMono
            })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to create sink');
        }

        if (remapSinkModalInstance) {
            remapSinkModalInstance.hide();
        }
        await refreshSinks();
        await refreshDevices(); // Custom sink should now appear in device list
        showAlert(`Remap sink "${name}" ${isEditing ? 'updated' : 'created'} successfully`, 'success');
    } catch (error) {
        showAlert(error.message, 'danger');
    }
}

// Delete sink
async function deleteSink(name) {
    if (!await showConfirm('Delete Sink', `Delete custom sink "${name}"?`, 'Delete', 'btn-danger')) return;

    try {
        const response = await fetch(`./api/sinks/${encodeURIComponent(name)}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to delete sink');
        }

        await refreshSinks();
        await refreshDevices();
        showAlert(`Sink "${name}" deleted`, 'success');
    } catch (error) {
        showAlert(error.message, 'danger');
    }
}

// Reload sink
async function reloadSink(name) {
    try {
        const response = await fetch(`./api/sinks/${encodeURIComponent(name)}/reload`, {
            method: 'POST'
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to reload sink');
        }

        await refreshSinks();
        showAlert(`Sink "${name}" reloaded`, 'success');
    } catch (error) {
        showAlert(error.message, 'danger');
    }
}

// Edit sink - opens the appropriate modal pre-filled with existing values
function editSink(name) {
    const sink = customSinks[name];
    if (!sink) {
        showAlert(`Sink "${name}" not found`, 'warning');
        return;
    }

    if (sink.type === 'Combine') {
        openCombineSinkModal(sink);
    } else if (sink.type === 'Remap') {
        openRemapSinkModal(sink);
    } else {
        showAlert(`Unknown sink type: ${sink.type}`, 'warning');
    }
}

// Play test tone for custom sink
async function playTestToneForSink(name) {
    const btn = document.getElementById(`sink-test-btn-${name}`);
    if (!btn) return;

    const originalContent = btn.innerHTML;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
    btn.disabled = true;

    try {
        const response = await fetch(`./api/sinks/${encodeURIComponent(name)}/test-tone`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ frequencyHz: 1000, durationMs: 1500 })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to play test tone');
        }
    } catch (error) {
        showAlert(`Test tone failed: ${error.message}`, 'danger');
    } finally {
        btn.innerHTML = originalContent;
        btn.disabled = false;
    }
}

// Play test tone for a specific channel in remap sink modal
async function playChannelTestTone(channel) {
    const masterSelect = document.getElementById('remapMasterDevice');
    let channelSelect, btn;

    if (channel === 'mono') {
        channelSelect = document.getElementById('monoChannel');
        btn = document.getElementById('monoChannelTestBtn');
    } else {
        channelSelect = document.getElementById(channel === 'left' ? 'leftChannel' : 'rightChannel');
        btn = document.getElementById(channel === 'left' ? 'leftChannelTestBtn' : 'rightChannelTestBtn');
    }

    if (!masterSelect.value) {
        showAlert('Please select a master device first', 'warning');
        return;
    }

    if (!btn) return;

    const originalContent = btn.innerHTML;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
    btn.disabled = true;
    btn.classList.add('playing');

    try {
        const response = await fetch(`./api/devices/${encodeURIComponent(masterSelect.value)}/test-tone`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                frequencyHz: 1000,
                durationMs: 1500,
                channelName: channelSelect.value
            })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to play test tone');
        }
    } catch (error) {
        showAlert(`Test tone failed: ${error.message}`, 'danger');
    } finally {
        btn.innerHTML = originalContent;
        btn.disabled = false;
        btn.classList.remove('playing');
    }
}

// Open import modal
async function openImportModal() {
    // Hide parent modal to avoid stacking issues
    if (customSinksModal) {
        customSinksModal.hide();
    }

    const list = document.getElementById('importSinksList');
    const empty = document.getElementById('importEmpty');
    const unavailable = document.getElementById('importUnavailable');
    const importBtn = document.getElementById('importBtn');

    // Reset state
    list.innerHTML = '<div class="text-center py-3"><i class="fas fa-spinner fa-spin"></i> Scanning...</div>';
    list.classList.remove('d-none');
    empty.classList.add('d-none');
    unavailable.classList.add('d-none');
    importBtn.disabled = false;

    // Get or create cached modal instance
    const modalEl = document.getElementById('importSinksModal');
    if (!importSinksModalInstance) {
        importSinksModalInstance = new bootstrap.Modal(modalEl);
        // Re-show parent modal when this one closes
        modalEl.addEventListener('hidden.bs.modal', () => {
            if (customSinksModal) {
                customSinksModal.show();
            }
        });
    }
    importSinksModalInstance.show();

    try {
        const response = await fetch('./api/sinks/import/scan');
        const data = await response.json();

        if (data.found === 0) {
            list.classList.add('d-none');
            empty.classList.remove('d-none');
            importBtn.disabled = true;
            return;
        }

        list.innerHTML = data.sinks.map(sink => `
            <div class="form-check border-bottom py-2">
                <input class="form-check-input" type="checkbox"
                       value="${sink.lineNumber}" id="import-${sink.lineNumber}">
                <label class="form-check-label w-100" for="import-${sink.lineNumber}">
                    <div class="d-flex justify-content-between">
                        <strong>${escapeHtml(sink.name)}</strong>
                        <span class="badge ${sink.type === 'Combine' ? 'bg-info' : 'bg-secondary'}">${sink.type}</span>
                    </div>
                    ${sink.description ? `<small class="text-muted">${escapeHtml(sink.description)}</small><br>` : ''}
                    <code class="small text-muted">${escapeHtml(sink.preview)}</code>
                </label>
            </div>
        `).join('');
    } catch (error) {
        list.classList.add('d-none');
        unavailable.classList.remove('d-none');
        importBtn.disabled = true;
    }
}

// Import selected sinks
async function importSelectedSinks() {
    const checkboxes = document.querySelectorAll('#importSinksList input:checked');
    const lineNumbers = Array.from(checkboxes).map(cb => parseInt(cb.value));

    if (lineNumbers.length === 0) {
        showAlert('Select at least one sink to import', 'warning');
        return;
    }

    // Disable button during import to prevent double-click
    const importBtn = document.getElementById('importBtn');
    importBtn.disabled = true;

    try {
        const response = await fetch('./api/sinks/import', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ lineNumbers })
        });

        const result = await response.json();

        if (result.imported && result.imported.length > 0) {
            showAlert(`Imported: ${result.imported.join(', ')}`, 'success');
        }
        if (result.errors && result.errors.length > 0) {
            showAlert(`Errors: ${result.errors.join('; ')}`, 'warning');
        }

        // Refresh before hiding modal to avoid race condition
        await refreshSinks();
        await refreshDevices();
        if (importSinksModalInstance) {
            importSinksModalInstance.hide();
        }
    } catch (error) {
        showAlert(error.message, 'danger');
    } finally {
        importBtn.disabled = false;
    }
}

// ============================================
// SOUND CARDS CONFIGURATION
// ============================================

let soundCardsModal = null;
let soundCards = [];
let soundCardDevices = []; // Devices associated with sound cards
let pendingDeviceAliases = {}; // Track pending alias changes: { deviceId: newAlias }
let expandedDeviceState = null; // Track which device accordion is expanded (single value - only one at a time)

// Open the sound cards configuration modal
async function openSoundCardsModal() {
    if (!soundCardsModal) {
        soundCardsModal = new bootstrap.Modal(document.getElementById('soundCardsModal'));
        // Save aliases and reset accordion state when modal is closed
        document.getElementById('soundCardsModal').addEventListener('hidden.bs.modal', () => {
            saveDeviceAliases();
            expandedDeviceState = null;
        });
    }

    // Clear pending aliases when opening
    pendingDeviceAliases = {};
    soundCardsModal.show();
    await loadSoundCards();
}

// Mark a device alias as changed (called from input onchange)
function markDeviceAliasChanged(input) {
    const deviceId = input.dataset.deviceId;
    const originalAlias = input.dataset.originalAlias;
    const newAlias = input.value.trim();

    // Only track if the alias actually changed
    if (newAlias !== originalAlias) {
        pendingDeviceAliases[deviceId] = newAlias;
    } else {
        delete pendingDeviceAliases[deviceId];
    }
}

// Handle keydown in alias input - stop propagation to prevent accordion toggle
async function handleAliasKeydown(event, input) {
    // Always stop propagation to prevent accordion from reacting to keys (especially spacebar)
    event.stopPropagation();

    if (event.key === 'Enter') {
        event.preventDefault();

        const deviceId = input.dataset.deviceId;
        const newAlias = input.value.trim();

        // Save immediately
        try {
            await fetch(`./api/devices/${encodeURIComponent(deviceId)}/alias`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ alias: newAlias || null })
            });
            // Update the original alias so it won't be re-saved on modal close
            input.dataset.originalAlias = newAlias;
            // Remove from pending since we just saved it
            delete pendingDeviceAliases[deviceId];
        } catch (error) {
            console.error(`Failed to save alias for device ${deviceId}:`, error);
        }

        // Exit edit mode (like clicking outside)
        input.blur();
    }
}

// Handle clicks on accordion header - manually toggle, but not when clicking alias input
function handleDeviceAccordionClick(event, targetId) {
    // Don't toggle if clicking on alias input (or inside it)
    // Also check activeElement for keyboard-triggered clicks (spacebar on button while input focused)
    const activeEl = document.activeElement;
    if (event.target.classList.contains('device-header-alias') ||
        event.target.closest('.device-header-alias') ||
        (activeEl && activeEl.classList.contains('device-header-alias'))) {
        event.stopPropagation();
        return;
    }

    // Manually toggle the Bootstrap collapse and update button state
    const collapse = document.getElementById(targetId);
    const button = event.currentTarget;
    if (collapse && button) {
        const isCurrentlyExpanded = collapse.classList.contains('show');
        const bsCollapse = bootstrap.Collapse.getOrCreateInstance(collapse);
        bsCollapse.toggle();
        // Update button's collapsed class for styling
        button.classList.toggle('collapsed', isCurrentlyExpanded);
    }
}

// Handle keydown on accordion button - block spacebar from toggling when typing in alias
function handleDeviceAccordionKeydown(event) {
    // If the event originated from the alias input, block spacebar from activating the button
    if (event.target.classList.contains('device-header-alias')) {
        if (event.key === ' ' || event.key === 'Spacebar') {
            // Stop the button from receiving this as an activation
            event.stopImmediatePropagation();
            // Don't preventDefault - let the space character be typed in the input
        }
        // Block Enter from activating the button (handleAliasKeydown handles Enter for saving)
        if (event.key === 'Enter') {
            event.stopImmediatePropagation();
        }
    }
}

// Save all pending device aliases using existing device alias API
async function saveDeviceAliases() {
    const entries = Object.entries(pendingDeviceAliases);
    if (entries.length === 0) {
        return;
    }

    for (const [deviceId, alias] of entries) {
        try {
            await fetch(`./api/devices/${encodeURIComponent(deviceId)}/alias`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ alias: alias || null })
            });
        } catch (error) {
            console.error(`Failed to save alias for device ${deviceId}:`, error);
        }
    }

    // Clear pending after save
    pendingDeviceAliases = {};

    // Refresh global devices so hardware dropdowns show updated aliases
    await refreshDevices();
}

// Load sound cards and their associated devices from API
async function loadSoundCards() {
    const container = document.getElementById('soundCardsContainer');
    const modalBody = document.querySelector('#soundCardsModal .modal-body');

    // Save expanded device state BEFORE replacing with loading spinner
    // Only capture from DOM if we have an existing state (i.e., refreshing within open modal)
    // If expandedDeviceState is null, we intentionally reset it (modal was closed/reopened)
    if (expandedDeviceState !== null) {
        const expandedItem = container.querySelector('.accordion-collapse.show');
        if (expandedItem) {
            const match = expandedItem.id.match(/^device-(.+)$/);
            if (match) {
                expandedDeviceState = match[1];
            }
        } else if (container.querySelector('.accordion')) {
            // Accordion exists but nothing is expanded - clear stale state
            expandedDeviceState = null;
        }
    }

    // Save scroll position
    const scrollTop = modalBody ? modalBody.scrollTop : 0;

    container.innerHTML = `
        <div class="text-center py-4">
            <div class="spinner-border text-primary" role="status">
                <span class="visually-hidden">Loading...</span>
            </div>
            <p class="mt-2 text-muted">Loading audio devices...</p>
        </div>
    `;

    try {
        // Fetch both cards and devices in parallel
        const [cardsResponse, devicesResponse] = await Promise.all([
            fetch('./api/cards'),
            fetch('./api/devices')
        ]);

        if (!cardsResponse.ok) {
            throw new Error('Failed to load audio devices');
        }
        if (!devicesResponse.ok) {
            throw new Error('Failed to load devices');
        }

        const cardsData = await cardsResponse.json();
        const devicesData = await devicesResponse.json();

        soundCards = cardsData.cards || [];
        soundCardDevices = devicesData.devices || [];

        renderSoundCards(scrollTop);
    } catch (error) {
        console.error('Error loading sound cards:', error);
        container.innerHTML = `
            <div class="alert alert-danger">
                <i class="fas fa-exclamation-triangle me-2"></i>
                Failed to load audio devices: ${escapeHtml(error.message)}
            </div>
        `;
    }
}

// Render sound cards list as accordion
function renderSoundCards(savedScrollTop = 0) {
    const container = document.getElementById('soundCardsContainer');
    const modalBody = document.querySelector('#soundCardsModal .modal-body');

    if (soundCards.length === 0) {
        container.innerHTML = `
            <div class="text-center py-4 text-muted">
                <i class="fas fa-sd-card fa-3x mb-3 opacity-50"></i>
                <p class="mb-0">No audio devices detected</p>
            </div>
        `;
        return;
    }

    // Determine which device should be expanded
    // Only auto-expand if exactly 1 device AND no saved state
    const shouldAutoExpand = soundCards.length === 1 && !expandedDeviceState;

    const accordionHtml = soundCards.map((card, index) => {
        const cardKey = card.index.toString();
        const isExpanded = expandedDeviceState === cardKey || (shouldAutoExpand && index === 0);

        const availableProfiles = card.profiles.filter(p => p.isAvailable);
        const hasMultipleProfiles = availableProfiles.length > 1;

        const profileOptions = availableProfiles.map(profile => {
            const isActive = profile.name === card.activeProfile;
            let label = profile.description || profile.name;
            if (profile.sinks > 0 || profile.sources > 0) {
                const parts = [];
                if (profile.sinks > 0) parts.push(`${profile.sinks} output${profile.sinks > 1 ? 's' : ''}`);
                if (profile.sources > 0) parts.push(`${profile.sources} input${profile.sources > 1 ? 's' : ''}`);
                label += ` (${parts.join(', ')})`;
            }
            return `<option value="${escapeHtml(profile.name)}" ${isActive ? 'selected' : ''}>${escapeHtml(label)}</option>`;
        }).join('');

        const activeProfile = availableProfiles.find(p => p.name === card.activeProfile);
        const activeDesc = activeProfile?.description || card.activeProfile;

        const muteState = getCardMuteDisplayState(card);
        const bootPreference = typeof card.bootMuted === 'boolean'
            ? (card.bootMuted ? 'muted' : 'unmuted')
            : 'unset';

        const busType = getCardBusType(card.name, card.activeProfile);
        const busIcon = getBusTypeIcon(busType);
        const busLabel = getBusTypeLabel(busType);

        // Find the device associated with this card by matching name patterns
        // For ALSA cards: alsa_card.pci-0000_00_1f.3 -> alsa_output.pci-0000_00_1f.3.analog-stereo
        // For Bluetooth: bluez_card.00_1A_7D_DA_71_13 -> bluez_sink.00_1A_7D_DA_71_13.a2dp_sink
        const cardBase = card.name.replace('alsa_card.', '').replace('bluez_card.', '');
        const device = soundCardDevices.find(d => d.id && d.id.includes(cardBase));
        const deviceAlias = device?.alias || '';
        const deviceId = device?.id || '';
        const deviceName = card.description || card.name;
        const maxVolumeDisplay = card.maxVolume !== null && card.maxVolume !== undefined ? card.maxVolume : 100;
        const isHidden = device?.hidden || false;

        return `
            <div class="accordion-item ${isHidden ? 'device-hidden' : ''}" id="device-item-${card.index}">
                <h2 class="accordion-header device-accordion-header">
                    ${isHidden ? '<div class="device-hidden-overlay"></div>' : ''}
                    <button class="accordion-button ${isExpanded ? '' : 'collapsed'}" type="button"
                            onclick="handleDeviceAccordionClick(event, 'device-${cardKey}')"
                            onkeydown="handleDeviceAccordionKeydown(event)">
                        <div class="device-header-content">
                            <span class="device-header-info">
                                <i class="${busIcon} text-primary me-2" title="${busLabel}"></i>
                                <span class="fw-bold">${escapeHtml(deviceName)}</span>
                                <input type="text" class="device-header-alias form-control form-control-sm ms-2"
                                       placeholder="Alias"
                                       value="${escapeHtml(deviceAlias)}"
                                       data-device-id="${escapeHtml(deviceId)}"
                                       data-original-alias="${escapeHtml(deviceAlias)}"
                                       ${deviceId ? '' : 'disabled title="No device found for this card"'}
                                       onchange="markDeviceAliasChanged(this)"
                                       onkeydown="handleAliasKeydown(event, this)">
                            </span>
                            <div class="device-header-profile">
                                <span class="badge bg-secondary text-truncate" id="device-profile-badge-${card.index}">${escapeHtml(activeDesc)}</span>
                            </div>
                        </div>
                    </button>
                    <span class="device-header-volume small text-muted">Max Vol: ${maxVolumeDisplay}%</span>
                    <span class="device-header-mute"
                          role="button"
                          tabindex="0"
                          title="${escapeHtml(muteState.label)}"
                          aria-label="${escapeHtml(muteState.label)}"
                          data-card-name="${escapeJsString(card.name)}"
                          data-card-index="${card.index}"
                          onclick="handleDeviceHeaderMute(event, this)">
                        <i class="fas ${muteState.icon} ${muteState.iconClass}"></i>
                    </span>
                </h2>
                <div id="device-${cardKey}" class="accordion-collapse collapse ${isExpanded ? 'show' : ''}"
                     data-bs-parent="#soundCardsAccordion">
                    <div class="accordion-body">
                        <!-- Row 1: Max Volume slider with driver info -->
                        <div class="mb-2">
                            <label class="form-label small text-muted mb-1">Max Volume</label>
                            <div class="d-flex align-items-center gap-2">
                                <input type="range" class="form-range flex-grow-1" min="0" max="100" step="1"
                                       value="${card.maxVolume || 100}"
                                       id="settings-max-volume-${card.index}"
                                       oninput="document.getElementById('settings-max-volume-value-${card.index}').textContent = this.value + '%'; updateDeviceHeaderVolume(${card.index}, this.value)"
                                       onchange="setDeviceMaxVolume('${escapeJsString(card.name)}', this.value, ${card.index})">
                                <span class="text-muted small" style="min-width: 40px;" id="settings-max-volume-value-${card.index}">${card.maxVolume || 100}%</span>
                            </div>
                        </div>

                        <!-- Row 2: Boot Mute | Audio Profile -->
                        <div class="d-flex flex-wrap align-items-center gap-3 mb-1">
                            <div class="d-flex align-items-center gap-2">
                                <label class="form-label small text-muted mb-0">Boot Mute</label>
                                <select class="form-select form-select-sm" style="width: auto;"
                                        id="settings-boot-mute-select-${card.index}"
                                        onchange="setSoundCardBootMute('${escapeJsString(card.name)}', this.value, ${card.index})">
                                    <option value="unset" ${bootPreference === 'unset' ? 'selected' : ''}>Not set</option>
                                    <option value="muted" ${bootPreference === 'muted' ? 'selected' : ''}>Muted</option>
                                    <option value="unmuted" ${bootPreference === 'unmuted' ? 'selected' : ''}>Unmuted</option>
                                </select>
                            </div>
                            ${hasMultipleProfiles ? `
                                <div class="d-flex align-items-center gap-2 flex-grow-1">
                                    <label class="form-label small text-muted mb-0">Profile</label>
                                    <select class="form-select form-select-sm flex-grow-1"
                                            id="settings-profile-select-${card.index}"
                                            onchange="setSoundCardProfile('${escapeJsString(card.name)}', this.value, ${card.index})">
                                        ${profileOptions}
                                    </select>
                                </div>
                            ` : `
                                <span class="text-muted small">
                                    <i class="fas fa-check-circle text-success me-1"></i>
                                    Profile: ${escapeHtml(activeDesc)}
                                </span>
                            `}
                        </div>
                        <div id="settings-card-message-${card.index}" class="small" style="min-height: 0;"></div>

                        <!-- Checkboxes -->
                        <div class="form-check mb-1">
                            <input class="form-check-input" type="checkbox"
                                   id="settings-device-hidden-${card.index}"
                                   ${device?.hidden ? 'checked' : ''}
                                   ${deviceId ? '' : 'disabled'}
                                   onchange="toggleDeviceHidden('${escapeJsString(deviceId)}', this.checked, ${card.index})">
                            <label class="form-check-label small" for="settings-device-hidden-${card.index}">
                                Hide from player and sink creation
                            </label>
                        </div>

                        <div class="form-check mb-2" id="settings-hid-buttons-container-${card.index}">
                            <input class="form-check-input" type="checkbox"
                                   id="settings-hid-buttons-${card.index}"
                                   disabled
                                   onchange="toggleHidButtons('${escapeJsString(deviceId)}', this.checked, ${card.index})">
                            <label class="form-check-label small" for="settings-hid-buttons-${card.index}">
                                Enable hardware volume/mute buttons
                            </label>
                            <span id="settings-hid-buttons-status-${card.index}" class="text-muted small ms-1"></span>
                        </div>

                        ${device?.capabilities ? `
                            <hr class="my-2" style="border-style: dotted; opacity: 0.3;">
                            <div class="small text-muted">
                                <span style="font-weight: 500;">Capabilities:</span>
                                ${device.capabilities.supportedSampleRates?.length > 0
                                    ? device.capabilities.supportedSampleRates.map(r => formatSampleRate(r)).join(' • ')
                                    : '?'}
                                &nbsp;|&nbsp;
                                ${device.capabilities.supportedBitDepths?.length > 0
                                    ? device.capabilities.supportedBitDepths.map(b => b + '-bit').join(' • ')
                                    : '?'}
                                &nbsp;|&nbsp;
                                ${device.capabilities.maxChannels
                                    ? device.capabilities.maxChannels + 'ch'
                                    : '?'}
                                ${device.capabilitySource === 'PulseAudioMax' ? ' <span class="badge bg-secondary" style="font-size: 0.65em;">inferred</span>' : ''}
                            </div>
                        ` : ''}
                        <div class="small text-muted mt-2" style="opacity: 0.7;">
                            <div style="font-weight: 500; margin-bottom: 0.25rem;">Device Info</div>
                            ${card.driver ? `<div><span class="device-info-label">Driver:</span> ${escapeHtml(card.driver)}</div>` : ''}
                            ${device?.identifiers?.bluetoothMac ? `<div><span class="device-info-label">MAC:</span> ${escapeHtml(device.identifiers.bluetoothMac)}</div>` : ''}
                            ${device?.identifiers?.bluetoothCodec ? `<div><span class="device-info-label">Codec:</span> ${escapeHtml(device.identifiers.bluetoothCodec).toUpperCase()}</div>` : ''}
                            ${device?.identifiers?.busPath ? `<div><span class="device-info-label">Bus:</span> ${escapeHtml(device.identifiers.busPath)}</div>` : ''}
                            ${deviceId ? `<div><span class="device-info-label">Sink:</span> ${escapeHtml(deviceId)}</div>` : ''}
                        </div>
                    </div>
                </div>
            </div>
        `;
    }).join('');

    container.innerHTML = `<div class="accordion" id="soundCardsAccordion">${accordionHtml}</div>`;

    // Add event listeners to sync button state with Bootstrap collapse events
    container.querySelectorAll('.accordion-collapse').forEach(collapse => {
        collapse.addEventListener('show.bs.collapse', () => {
            const button = collapse.closest('.accordion-item')?.querySelector('.accordion-button');
            if (button) button.classList.remove('collapsed');
        });
        collapse.addEventListener('hide.bs.collapse', () => {
            const button = collapse.closest('.accordion-item')?.querySelector('.accordion-button');
            if (button) button.classList.add('collapsed');
        });
    });

    // Update expandedDeviceState to match current DOM state if we auto-expanded
    if (shouldAutoExpand) {
        expandedDeviceState = soundCards[0].index.toString();
    }

    // Restore scroll position
    if (modalBody && savedScrollTop > 0) {
        requestAnimationFrame(() => {
            modalBody.scrollTop = savedScrollTop;
        });
    }

    // Check HID button availability for each card
    soundCards.forEach(card => {
        const cardBase = card.name.replace('alsa_card.', '').replace('bluez_card.', '');
        const device = soundCardDevices.find(d => d.id && d.id.includes(cardBase));
        const hidContainer = document.getElementById(`settings-hid-buttons-container-${card.index}`);

        if (device?.id) {
            checkHidButtonStatus(device.id, card.index);
        } else {
            // No matching device found (e.g., card profile is "off") - hide HID option
            if (hidContainer) {
                hidContainer.style.display = 'none';
            }
        }
    });
}

// Handle mute button click in device accordion header
// Prevents accordion toggle and calls the mute function
function handleDeviceHeaderMute(event, button) {
    // Stop all event propagation to prevent accordion toggle
    event.stopPropagation();
    event.stopImmediatePropagation();
    event.preventDefault();

    // Prevent rapid clicks while API call is in flight (span doesn't support disabled)
    if (button.dataset.busy === 'true') {
        return false;
    }

    const cardName = button.dataset.cardName;
    const cardIndex = parseInt(button.dataset.cardIndex, 10);

    toggleSoundCardMute(cardName, cardIndex);

    return false;
}

// Update the max volume display in the device accordion header
function updateDeviceHeaderVolume(cardIndex, value) {
    const volumeSpan = document.querySelector(`#device-item-${cardIndex} .device-header-volume`);
    if (volumeSpan) {
        volumeSpan.textContent = `Max Vol: ${value}%`;
    }
}

function getCardMuteDisplayState(card) {
    if (typeof card.isMuted !== 'boolean') {
        return {
            isMuted: false,
            label: 'Mute status unknown',
            icon: 'fa-question-circle',
            iconClass: 'text-muted'
        };
    }

    const isMuted = card.isMuted;
    return {
        isMuted,
        label: isMuted ? 'Muted' : 'Unmuted',
        icon: isMuted ? 'fa-volume-mute' : 'fa-volume-up',
        iconClass: isMuted ? 'text-danger' : 'text-success'
    };
}

async function setSoundCardMute(cardName, muted, cardIndex) {
    // Find the mute button in the accordion header
    const button = document.querySelector(`#device-item-${cardIndex} .device-header-mute`);

    // Use data-busy attribute since span doesn't support disabled
    if (button) button.dataset.busy = 'true';

    try {
        const response = await fetch(`./api/cards/${encodeURIComponent(cardName)}/mute`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ muted })
        });

        const result = await response.json();
        if (!response.ok) {
            throw new Error(result.message || 'Failed to set mute');
        }

        const card = soundCards.find(c => c.name === cardName);
        if (card) {
            card.isMuted = muted;
            card.bootMuteMatchesCurrent = typeof card.bootMuted === 'boolean' && card.bootMuted === muted;
        }

        const muteState = getCardMuteDisplayState(card || { isMuted: muted, bootMuted: null, bootMuteMatchesCurrent: false });
        if (button) {
            const icon = button.querySelector('i');
            if (icon) {
                icon.className = `fas ${muteState.icon} ${muteState.iconClass}`;
            }
            button.setAttribute('aria-label', muteState.label);
            button.setAttribute('title', muteState.label);
        }
    } catch (error) {
        console.error('Failed to set card mute:', error);
        showAlert(error.message, 'danger');
    } finally {
        if (button) delete button.dataset.busy;
    }
}

function toggleSoundCardMute(cardName, cardIndex) {
    const card = soundCards.find(c => c.name === cardName);
    const isMuted = typeof card?.isMuted === 'boolean' ? card.isMuted : false;
    return setSoundCardMute(cardName, !isMuted, cardIndex);
}

async function setSoundCardBootMute(cardName, value, cardIndex) {
    const select = document.getElementById(`settings-boot-mute-select-${cardIndex}`);
    if (!select || value === 'unset') {
        return;
    }

    select.disabled = true;

    try {
        const muted = value === 'muted';
        const response = await fetch(`./api/cards/${encodeURIComponent(cardName)}/boot-mute`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ muted })
        });

        const result = await response.json();
        if (!response.ok) {
            throw new Error(result.message || 'Failed to set boot mute');
        }

        const card = soundCards.find(c => c.name === cardName);
        if (card) {
            card.bootMuted = muted;
            card.bootMuteMatchesCurrent = typeof card.isMuted === 'boolean' && card.isMuted === muted;
        }
        // Note: Boot mute doesn't affect the header mute button - it only shows current state
    } catch (error) {
        console.error('Failed to set boot mute:', error);
        showAlert(error.message, 'danger');
        const card = soundCards.find(c => c.name === cardName);
        if (card && select) {
            const fallback = typeof card.bootMuted === 'boolean'
                ? (card.bootMuted ? 'muted' : 'unmuted')
                : 'unset';
            select.value = fallback;
        }
    } finally {
        if (select) select.disabled = false;
    }
}

async function setDeviceMaxVolume(cardName, maxVolume, cardIndex) {
    const slider = document.getElementById(`settings-max-volume-${cardIndex}`);
    if (slider) slider.disabled = true;

    try {
        const volumeValue = parseInt(maxVolume);
        const response = await fetch(`./api/cards/${encodeURIComponent(cardName)}/max-volume`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ maxVolume: volumeValue })
        });

        const result = await response.json();
        if (!response.ok) {
            throw new Error(result.message || 'Failed to set max volume');
        }

        const card = soundCards.find(c => c.name === cardName);
        if (card) {
            card.maxVolume = volumeValue;
        }

        showAlert(`Max volume set to ${volumeValue}%`, 'success', 2000);
    } catch (error) {
        console.error('Failed to set max volume:', error);
        showAlert(error.message, 'danger');
        // Revert slider to previous value
        const card = soundCards.find(c => c.name === cardName);
        if (slider && card) {
            slider.value = card.maxVolume || 100;
            const valueDisplay = document.getElementById(`settings-max-volume-value-${cardIndex}`);
            if (valueDisplay) {
                valueDisplay.textContent = (card.maxVolume || 100) + '%';
            }
        }
    } finally {
        if (slider) slider.disabled = false;
    }
}

// Toggle device hidden state (affects both player and sink creation)
async function toggleDeviceHidden(deviceId, hidden, cardIndex) {
    const checkbox = document.getElementById(`settings-device-hidden-${cardIndex}`);
    if (!checkbox || !deviceId) return;

    checkbox.disabled = true;

    try {
        const response = await fetch(`./api/devices/${encodeURIComponent(deviceId)}/hidden`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ hidden })
        });

        const result = await response.json();
        if (!response.ok) {
            throw new Error(result.message || 'Failed to set hidden state');
        }

        // Update local device state
        const device = soundCardDevices.find(d => d.id === deviceId);
        if (device) {
            device.hidden = hidden;
        }

        // Update visual treatment for hidden state
        const accordionItem = document.getElementById(`device-item-${cardIndex}`);
        const accordionHeader = accordionItem?.querySelector('.accordion-header');
        if (accordionItem && accordionHeader) {
            if (hidden) {
                // Add hidden styling
                accordionItem.classList.add('device-hidden');
                if (!accordionHeader.querySelector('.device-hidden-overlay')) {
                    const overlay = document.createElement('div');
                    overlay.className = 'device-hidden-overlay';
                    accordionHeader.insertBefore(overlay, accordionHeader.firstChild);
                }
            } else {
                // Remove hidden styling
                accordionItem.classList.remove('device-hidden');
                const overlay = accordionHeader.querySelector('.device-hidden-overlay');
                if (overlay) overlay.remove();
            }
        }

        // Refresh global devices array so sink/player creation modals reflect the change
        await refreshDevices();

        showAlert(hidden ? 'Device hidden' : 'Device visible', 'success', 2000);
    } catch (error) {
        console.error('Failed to set hidden state:', error);
        showAlert(error.message, 'danger');
        // Revert checkbox on error
        checkbox.checked = !hidden;
    } finally {
        checkbox.disabled = false;
    }
}

// Check HID button status for a device
async function checkHidButtonStatus(deviceId, cardIndex) {
    const checkbox = document.getElementById(`settings-hid-buttons-${cardIndex}`);
    const statusSpan = document.getElementById(`settings-hid-buttons-status-${cardIndex}`);
    const container = document.getElementById(`settings-hid-buttons-container-${cardIndex}`);

    if (!deviceId || !checkbox) {
        if (container) container.style.display = 'none';
        return;
    }

    try {
        const response = await fetch(`./api/devices/${encodeURIComponent(deviceId)}/hid-status`);
        if (!response.ok) {
            if (container) container.style.display = 'none';
            return;
        }

        const status = await response.json();

        if (!status.hidButtonsAvailable) {
            // No HID buttons available - hide the option
            if (container) container.style.display = 'none';
            return;
        }

        // HID buttons are available
        if (container) container.style.display = '';
        checkbox.disabled = false;
        checkbox.checked = status.hidButtonsEnabled;

        if (statusSpan) {
            if (status.hidButtonsEnabled && status.inputDevicePath) {
                statusSpan.textContent = '(active)';
                statusSpan.className = 'text-success small ms-1';
            } else {
                statusSpan.textContent = '';
            }
        }
    } catch (error) {
        console.error('Failed to check HID button status:', error);
        if (container) container.style.display = 'none';
    }
}

// Toggle HID button support for a device
async function toggleHidButtons(deviceId, enabled, cardIndex) {
    const checkbox = document.getElementById(`settings-hid-buttons-${cardIndex}`);
    const statusSpan = document.getElementById(`settings-hid-buttons-status-${cardIndex}`);

    if (!checkbox || !deviceId) return;

    checkbox.disabled = true;
    if (statusSpan) {
        statusSpan.textContent = enabled ? '(enabling...)' : '(disabling...)';
        statusSpan.className = 'text-muted small ms-1';
    }

    try {
        const response = await fetch(`./api/devices/${encodeURIComponent(deviceId)}/hid-buttons`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled })
        });

        const result = await response.json();

        if (!response.ok) {
            throw new Error(result.message || 'Failed to toggle HID buttons');
        }

        checkbox.checked = result.hidButtonsEnabled;

        if (statusSpan) {
            if (result.hidButtonsEnabled) {
                statusSpan.textContent = '(active)';
                statusSpan.className = 'text-success small ms-1';
            } else {
                statusSpan.textContent = '';
            }
        }

        showAlert(result.message, result.success ? 'success' : 'warning', 3000);
    } catch (error) {
        console.error('Failed to toggle HID buttons:', error);
        showAlert(error.message, 'danger');
        // Revert checkbox on error
        checkbox.checked = !enabled;
        if (statusSpan) statusSpan.textContent = '';
    } finally {
        checkbox.disabled = false;
    }
}

// Set a sound card's profile
async function setSoundCardProfile(cardName, profileName, cardIndex) {
    const select = document.getElementById(`settings-profile-select-${cardIndex}`);
    const statusBadge = document.getElementById(`device-profile-badge-${cardIndex}`);
    const messageDiv = document.getElementById(`settings-card-message-${cardIndex}`);

    if (select) select.disabled = true;
    if (messageDiv) {
        messageDiv.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>Changing profile...';
        messageDiv.className = 'small text-muted';
    }

    try {
        const response = await fetch(`./api/cards/${encodeURIComponent(cardName)}/profile`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ profile: profileName })
        });

        const result = await response.json();

        if (!response.ok) {
            throw new Error(result.message || 'Failed to change profile');
        }

        // Update local state
        const card = soundCards.find(c => c.name === cardName);
        if (card) {
            card.activeProfile = profileName;
            const profile = card.profiles.find(p => p.name === profileName);
            if (statusBadge && profile) {
                statusBadge.textContent = profile.description || profileName;
            }
        }

        if (messageDiv) {
            messageDiv.innerHTML = '<i class="fas fa-check text-success me-1"></i>Profile changed successfully';
            messageDiv.className = 'small text-success';
            setTimeout(() => { messageDiv.innerHTML = ''; }, 3000);
        }

        // Refresh devices since profile change may affect available outputs
        await refreshDevices();

    } catch (error) {
        console.error('Failed to set card profile:', error);
        if (messageDiv) {
            messageDiv.innerHTML = `<i class="fas fa-exclamation-triangle me-1"></i>${escapeHtml(error.message)}`;
            messageDiv.className = 'small text-danger';
        }
        // Revert select to previous value
        const card = soundCards.find(c => c.name === cardName);
        if (select && card) {
            select.value = card.activeProfile;
        }
    } finally {
        if (select) select.disabled = false;
    }
}

// ============================================
// LOGS VIEWER
// ============================================

let logsData = [];
let logsSkip = 0;
const logsPageSize = 100;
let logsConnection = null;
let logsAutoScroll = true;
let logsLiveStream = true;
let logSearchTimeout = null;

// Switch to logs view
function showLogsView() {
    document.getElementById('playersView').classList.add('d-none');
    document.getElementById('logsView').classList.remove('d-none');

    // Initialize logs
    logsData = [];
    logsSkip = 0;
    refreshLogs();
    setupLogsSignalR();
}

// Switch back to players view
function showPlayersView() {
    document.getElementById('logsView').classList.add('d-none');
    document.getElementById('playersView').classList.remove('d-none');

    // Cleanup SignalR connection
    if (logsConnection) {
        logsConnection.stop();
        logsConnection = null;
    }
}

// Setup SignalR for log streaming
function setupLogsSignalR() {
    if (typeof signalR === 'undefined' || logsConnection) return;

    logsConnection = new signalR.HubConnectionBuilder()
        .withUrl('./hubs/logs')
        .withAutomaticReconnect()
        .build();

    logsConnection.on('LogEntry', (entry) => {
        if (!logsLiveStream) return;

        // Check filters
        const levelFilter = document.getElementById('logLevelFilter').value;
        const categoryFilter = document.getElementById('logCategoryFilter').value;
        const searchFilter = document.getElementById('logSearchInput').value.toLowerCase();

        if (levelFilter && entry.level.toLowerCase() !== levelFilter) return;
        if (categoryFilter && entry.category !== categoryFilter) return;
        if (searchFilter && !entry.message.toLowerCase().includes(searchFilter)) return;

        // Add to top of list (newest first)
        logsData.unshift(entry);
        // Limit array size to prevent memory leak (matches DOM limit of 500)
        if (logsData.length > 500) {
            logsData.pop();
        }
        prependLogEntry(entry);
        updateLogsCount();

        // Auto-scroll to top if enabled
        if (logsAutoScroll) {
            document.getElementById('logsContainer').scrollTop = 0;
        }
    });

    logsConnection.on('InitialLogs', (entries) => {
        // Initial logs are sent on connection, but we already load via API
        // This is just for quick population if needed
    });

    logsConnection.start().catch(err => {
        console.log('Logs SignalR connection failed:', err);
    });
}

// Debounced search
function debouncedLogSearch() {
    if (logSearchTimeout) {
        clearTimeout(logSearchTimeout);
    }
    logSearchTimeout = setTimeout(refreshLogs, 300);
}

// Refresh logs (reset and reload)
async function refreshLogs() {
    logsSkip = 0;
    logsData = [];

    const container = document.getElementById('logsContainer');
    container.innerHTML = `
        <div class="text-center py-5 text-muted">
            <i class="fas fa-spinner fa-spin fa-2x mb-3"></i>
            <p>Loading logs...</p>
        </div>
    `;

    await loadLogs();
}

// Load logs from API
async function loadLogs() {
    const level = document.getElementById('logLevelFilter').value;
    const category = document.getElementById('logCategoryFilter').value;
    const search = document.getElementById('logSearchInput').value;

    const params = new URLSearchParams({
        skip: logsSkip,
        take: logsPageSize,
        newestFirst: true
    });

    if (level) params.append('level', level);
    if (category) params.append('category', category);
    if (search) params.append('search', search);

    try {
        const response = await fetch(`./api/logs?${params}`);
        if (!response.ok) throw new Error('Failed to fetch logs');

        const data = await response.json();

        if (logsSkip === 0) {
            logsData = data.entries;
            renderLogs();
        } else {
            logsData = [...logsData, ...data.entries];
            appendLogEntries(data.entries);
        }

        // Show/hide load more button
        const loadMore = document.getElementById('logsLoadMore');
        if (logsData.length < data.totalCount) {
            loadMore.classList.remove('d-none');
        } else {
            loadMore.classList.add('d-none');
        }

        updateLogsCount(data.totalCount);

    } catch (error) {
        console.error('Error loading logs:', error);
        const container = document.getElementById('logsContainer');
        container.innerHTML = `
            <div class="text-center py-5 text-danger">
                <i class="fas fa-exclamation-triangle fa-2x mb-3"></i>
                <p>Failed to load logs</p>
            </div>
        `;
    }
}

// Load more logs (pagination)
function loadMoreLogs() {
    logsSkip += logsPageSize;
    loadLogs();
}

// Render all logs
function renderLogs() {
    const container = document.getElementById('logsContainer');

    if (logsData.length === 0) {
        container.innerHTML = `
            <div class="text-center py-5 text-muted empty-state">
                <i class="fas fa-scroll fa-3x mb-3 opacity-50"></i>
                <h5>No logs found</h5>
                <p>Logs will appear here as the application runs.</p>
            </div>
        `;
        return;
    }

    container.innerHTML = logsData.map(entry => createLogEntryHtml(entry)).join('');
}

// Create HTML for a single log entry
function createLogEntryHtml(entry, isNew = false) {
    const time = new Date(entry.timestamp).toLocaleTimeString('en-US', {
        hour12: false,
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit'
    });

    const levelClass = entry.level.toLowerCase();

    return `
        <div class="log-entry${isNew ? ' new' : ''}">
            <span class="log-timestamp">${escapeHtml(time)}</span>
            <span class="log-level ${levelClass}">${escapeHtml(entry.level)}</span>
            <span class="log-category">${escapeHtml(entry.category)}</span>
            <div class="log-message">
                ${escapeHtml(entry.message)}
                ${entry.exception ? `<div class="log-exception">${escapeHtml(entry.exception)}</div>` : ''}
            </div>
        </div>
    `;
}

// Prepend a single log entry (for live streaming)
function prependLogEntry(entry) {
    const container = document.getElementById('logsContainer');
    const emptyState = container.querySelector('.empty-state');
    if (emptyState) {
        container.innerHTML = '';
    }

    const html = createLogEntryHtml(entry, true);
    container.insertAdjacentHTML('afterbegin', html);

    // Limit displayed entries for performance
    const entries = container.querySelectorAll('.log-entry');
    if (entries.length > 500) {
        entries[entries.length - 1].remove();
    }
}

// Append log entries (for pagination)
function appendLogEntries(entries) {
    const container = document.getElementById('logsContainer');
    const html = entries.map(e => createLogEntryHtml(e)).join('');
    container.insertAdjacentHTML('beforeend', html);
}

// Update the logs count badge
function updateLogsCount(total) {
    const countBadge = document.getElementById('logsCount');
    countBadge.textContent = (total !== undefined ? total : logsData.length).toLocaleString();
}

// Clear all logs
async function clearLogs() {
    if (!await showConfirm('Clear Logs', 'Clear all logs? This cannot be undone.', 'Clear', 'btn-danger')) return;

    try {
        const response = await fetch('./api/logs', { method: 'DELETE' });
        if (!response.ok) throw new Error('Failed to clear logs');

        logsData = [];
        logsSkip = 0;
        renderLogs();
        updateLogsCount(0);
        showAlert('Logs cleared', 'success');
    } catch (error) {
        showAlert('Failed to clear logs', 'danger');
    }
}

// Download logs as text file
function downloadLogs() {
    // Build query string with current filters
    const level = document.getElementById('logLevelFilter').value;
    const category = document.getElementById('logCategoryFilter').value;
    const search = document.getElementById('logSearchInput').value;

    const params = new URLSearchParams();
    if (level) params.append('level', level);
    if (category) params.append('category', category);
    if (search) params.append('search', search);

    // Trigger download via browser (server handles file generation)
    window.location.href = `./api/logs/download?${params}`;
}

// Active diagnostics EventSource (for cancellation support)
let activeDiagnosticsEventSource = null;

// Download comprehensive diagnostics file with progress banner
function downloadDiagnostics() {
    const banner = document.getElementById('diagnostics-banner');
    const bannerText = document.getElementById('diagnostics-banner-text');
    const progressEl = document.getElementById('diagnostics-progress');

    if (!banner) {
        // Fallback to direct download if banner doesn't exist
        window.location.href = './api/diagnostics/download';
        return;
    }

    // Cancel any existing diagnostics generation
    if (activeDiagnosticsEventSource) {
        activeDiagnosticsEventSource.close();
        activeDiagnosticsEventSource = null;
    }

    // Show the banner
    banner.classList.remove('d-none');
    bannerText.textContent = 'Generating diagnostics...';
    progressEl.textContent = '';

    // Connect to SSE endpoint
    const eventSource = new EventSource('./api/diagnostics/stream');
    activeDiagnosticsEventSource = eventSource;

    eventSource.addEventListener('progress', (event) => {
        const data = JSON.parse(event.data);
        bannerText.textContent = `Generating diagnostics — ${data.phase}`;
        progressEl.textContent = `(${data.current}/${data.total})`;
    });

    eventSource.addEventListener('complete', (event) => {
        eventSource.close();
        activeDiagnosticsEventSource = null;

        // Decode base64 content to bytes (preserves UTF-8 encoding)
        const base64Content = event.data;
        const binaryString = atob(base64Content);
        const bytes = new Uint8Array(binaryString.length);
        for (let i = 0; i < binaryString.length; i++) {
            bytes[i] = binaryString.charCodeAt(i);
        }

        // Create blob and trigger download
        const blob = new Blob([bytes], { type: 'text/plain; charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const filename = `multiroom-diagnostics-${new Date().toISOString().slice(0, 19).replace(/[T:]/g, '-')}.txt`;

        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);

        // Hide the banner
        banner.classList.add('d-none');
    });

    eventSource.addEventListener('error', (event) => {
        eventSource.close();
        activeDiagnosticsEventSource = null;
        bannerText.textContent = 'Diagnostics generation failed';
        progressEl.textContent = '';

        // Hide after 3 seconds
        setTimeout(() => {
            banner.classList.add('d-none');
        }, 3000);

        showAlert('Failed to generate diagnostics', 'danger');
    });

    // Also handle connection errors
    eventSource.onerror = () => {
        if (eventSource.readyState === EventSource.CLOSED) {
            return; // Already handled by 'error' event
        }
        eventSource.close();
        activeDiagnosticsEventSource = null;
        banner.classList.add('d-none');
        showAlert('Connection lost while generating diagnostics', 'danger');
    };
}

// Cancel diagnostics generation
function cancelDiagnostics() {
    const banner = document.getElementById('diagnostics-banner');

    if (activeDiagnosticsEventSource) {
        activeDiagnosticsEventSource.close();
        activeDiagnosticsEventSource = null;
    }

    if (banner) {
        banner.classList.add('d-none');
    }
}

// ============================================
// ONBOARDING WIZARD INTEGRATION
// ============================================

// Run the setup wizard manually
function runSetupWizard() {
    if (typeof OnboardingWizard !== 'undefined') {
        OnboardingWizard.start();
    } else {
        showAlert('Setup wizard is not available', 'warning');
    }
}

// Reset first-run state to allow wizard to show again
async function resetOnboarding() {
    if (!await showConfirm('Reset Wizard', 'Reset first-run state? The setup wizard will appear on the next page load.', 'Reset', 'btn-primary')) {
        return;
    }

    try {
        const response = await fetch('./api/onboarding/reset', {
            method: 'POST'
        });

        if (!response.ok) {
            throw new Error('Failed to reset onboarding state');
        }

        showAlert('First-run state reset. Refresh the page to see the wizard.', 'success');
    } catch (error) {
        console.error('Error resetting onboarding:', error);
        showAlert(error.message, 'danger');
    }
}

// Toggle handlers for switches
document.addEventListener('DOMContentLoaded', () => {
    const autoScrollSwitch = document.getElementById('logAutoScroll');
    const liveStreamSwitch = document.getElementById('logLiveStream');

    if (autoScrollSwitch) {
        autoScrollSwitch.addEventListener('change', (e) => {
            logsAutoScroll = e.target.checked;
        });
    }

    if (liveStreamSwitch) {
        liveStreamSwitch.addEventListener('change', (e) => {
            logsLiveStream = e.target.checked;
        });
    }
});

// ============================================
// 12V TRIGGERS CONFIGURATION (Multi-Board Support)
// ============================================

let triggersModal = null;
let addBoardModal = null;
let triggersData = null;
let customSinksList = [];
let ftdiDevicesData = null;
let triggersRefreshInterval = null;
let triggersOperationCount = 0; // Counter to prevent refresh interval from clobbering state during overlapping operations

// Open the triggers configuration modal
async function openTriggersModal() {
    if (!triggersModal) {
        triggersModal = new bootstrap.Modal(document.getElementById('triggersModal'));

        // Set up auto-refresh when modal is shown/hidden
        document.getElementById('triggersModal').addEventListener('shown.bs.modal', () => {
            triggersRefreshInterval = setInterval(refreshTriggersState, 2000);
        });
        document.getElementById('triggersModal').addEventListener('hidden.bs.modal', () => {
            if (triggersRefreshInterval) {
                clearInterval(triggersRefreshInterval);
                triggersRefreshInterval = null;
            }
        });
    }

    triggersModal.show();
    await loadTriggers();
}

// Refresh only the trigger states (lightweight update without full reload)
async function refreshTriggersState() {
    // Skip refresh if an operation is in progress to prevent state clobbering
    if (triggersOperationCount > 0) {
        return;
    }
    try {
        const response = await fetch('./api/triggers');
        if (response.ok) {
            const newData = await response.json();
            // Recheck after await - an operation may have started while we were fetching
            if (triggersOperationCount > 0) {
                return;
            }
            if (JSON.stringify(newData.boards) !== JSON.stringify(triggersData?.boards)) {
                triggersData = newData;
                renderTriggers();
            }
        }
    } catch (error) {
        console.debug('Error refreshing triggers state:', error);
    }
}

// Track which accordion boards are expanded (persists across loadTriggers calls)
let expandedBoardsState = new Set();
// Track scroll position for triggers modal (persists across loadTriggers calls)
let triggersScrollPosition = 0;

// Load triggers status and custom sinks from API
async function loadTriggers() {
    const container = document.getElementById('triggersContainer');
    const modalBody = document.querySelector('#triggersModal .modal-body');

    // Save scroll position BEFORE replacing with loading spinner
    if (modalBody) {
        triggersScrollPosition = modalBody.scrollTop;
    }

    // Save expanded boards state BEFORE replacing with loading spinner
    container.querySelectorAll('.accordion-collapse.show').forEach(el => {
        const match = el.id.match(/^board-(.+)$/);
        if (match) {
            expandedBoardsState.add(match[1]);
        }
    });

    container.innerHTML = `
        <div class="text-center py-4">
            <div class="spinner-border text-primary" role="status">
                <span class="visually-hidden">Loading...</span>
            </div>
            <p class="mt-2 text-muted">Loading triggers...</p>
        </div>
    `;

    try {
        const [triggersResponse, sinksResponse, devicesResponse] = await Promise.all([
            fetch('./api/triggers'),
            fetch('./api/sinks'),
            fetch('./api/triggers/devices/all')
        ]);

        if (!triggersResponse.ok) throw new Error('Failed to load triggers');
        if (!sinksResponse.ok) throw new Error('Failed to load custom sinks');

        triggersData = await triggersResponse.json();
        const sinksData = await sinksResponse.json();
        customSinksList = sinksData.sinks || [];

        if (devicesResponse.ok) {
            // Store unified device data (includes both FTDI and HID)
            ftdiDevicesData = await devicesResponse.json();
            // Ensure backwards compatibility - treat count > 0 as library available
            if (ftdiDevicesData.count > 0) {
                ftdiDevicesData.libraryAvailable = true;
            }
        } else {
            ftdiDevicesData = { devices: [], count: 0, ftdiCount: 0, hidCount: 0 };
        }

        renderTriggers();
    } catch (error) {
        console.error('Error loading triggers:', error);
        container.innerHTML = `
            <div class="alert alert-danger">
                <i class="fas fa-exclamation-triangle me-2"></i>
                Failed to load triggers: ${escapeHtml(error.message)}
            </div>
        `;
    }
}

// Render triggers list (multi-board accordion)
function renderTriggers() {
    const container = document.getElementById('triggersContainer');
    const enabledCheckbox = document.getElementById('triggersEnabled');
    const foundBadge = document.getElementById('triggerFoundBadge');
    const connectedBadge = document.getElementById('triggerConnectedBadge');
    const errorDiv = document.getElementById('triggersError');
    const errorText = document.getElementById('triggersErrorText');
    const totalChannelsSpan = document.getElementById('triggersTotalChannels');

    // Save scroll position of modal body before re-rendering
    // Use saved position from loadTriggers if available (DOM was cleared by loading spinner)
    const modalBody = document.querySelector('#triggersModal .modal-body');
    const currentScrollTop = modalBody ? modalBody.scrollTop : 0;
    const scrollTop = currentScrollTop > 0 ? currentScrollTop : triggersScrollPosition;
    // Reset the saved position after using it
    triggersScrollPosition = 0;

    // Save which accordion items are currently expanded (by boardId)
    // First check the current DOM, then fall back to the global state (for when DOM was cleared by loading spinner)
    const expandedBoards = new Set();
    container.querySelectorAll('.accordion-collapse.show').forEach(el => {
        // Extract boardId from the element id (format: board-{boardIdSafe})
        const match = el.id.match(/^board-(.+)$/);
        if (match) {
            expandedBoards.add(match[1]);
        }
    });

    // If no expanded boards found in DOM, use the global saved state
    if (expandedBoards.size === 0 && expandedBoardsState.size > 0) {
        expandedBoardsState.forEach(id => expandedBoards.add(id));
    }

    // Update global state to match current state (for next loadTriggers call)
    expandedBoardsState = new Set(expandedBoards);

    if (!triggersData) {
        container.innerHTML = '<div class="text-center py-4 text-muted">No trigger data available</div>';
        return;
    }

    const noHardware = ftdiDevicesData && !ftdiDevicesData.libraryAvailable && ftdiDevicesData.count === 0;
    const noDevicesDetected = ftdiDevicesData && ftdiDevicesData.libraryAvailable && ftdiDevicesData.count === 0;

    // Update enabled checkbox
    enabledCheckbox.checked = triggersData.enabled;
    enabledCheckbox.disabled = noHardware;

    // Update total channels
    if (totalChannelsSpan) {
        totalChannelsSpan.textContent = triggersData.boards.length > 0
            ? `${triggersData.totalChannels} channels across ${triggersData.boards.length} board(s)`
            : '';
    }

    // Update status badges - show both "Found" and "Connected" counts
    const detectedDeviceCount = ftdiDevicesData?.count || 0;

    // "N Devices Found" badge
    if (noHardware) {
        foundBadge.classList.add('d-none');
    } else {
        foundBadge.classList.remove('d-none');
        foundBadge.textContent = `${detectedDeviceCount} Device${detectedDeviceCount !== 1 ? 's' : ''} Found`;
        foundBadge.className = `badge ${detectedDeviceCount > 0 ? 'bg-info' : 'bg-secondary'} me-2`;
    }

    // "N Devices Connected" badge
    const configuredCount = triggersData.boards.length;
    const connectedCount = triggersData.boards.filter(b => b.isConnected).length;

    if (configuredCount === 0 || noHardware || !triggersData.enabled) {
        connectedBadge.classList.add('d-none');
    } else {
        connectedBadge.classList.remove('d-none');
        connectedBadge.textContent = `${connectedCount} Device${connectedCount !== 1 ? 's' : ''} Connected`;

        let connectedClass = 'bg-success';
        if (connectedCount === 0) {
            connectedClass = 'bg-danger';
        } else if (connectedCount < configuredCount) {
            connectedClass = 'bg-warning';
        }
        connectedBadge.className = `badge ${connectedClass}`;
    }

    // Show/hide error - only show when truly no hardware support
    if (noHardware) {
        errorDiv.classList.remove('d-none');
        errorText.textContent = 'No relay board hardware support detected. Install libftdi1 for FTDI boards or ensure USB HID support is available.';
    } else {
        errorDiv.classList.add('d-none');
    }

    // Build sink options HTML
    const sinkOptions = customSinksList.map(sink =>
        `<option value="${escapeHtml(sink.name)}">${escapeHtml(sink.description || sink.name)}</option>`
    ).join('');

    // Build accordion for each board
    if (triggersData.boards.length === 0) {
        container.innerHTML = `
            <div class="text-center py-4 text-muted">
                <i class="fas fa-plug fa-2x mb-2"></i>
                <p>No relay boards configured. Click "Add Relay Board" to get started.</p>
            </div>
        `;
        return;
    }

    // Only auto-expand first item if there's exactly 1 board AND no saved state
    const shouldAutoExpand = triggersData.boards.length === 1 && expandedBoards.size === 0;

    const accordionHtml = triggersData.boards.map((board, index) => {
        const boardId = escapeHtml(board.boardId);
        const boardIdSafe = board.boardId.replace(/[^a-zA-Z0-9]/g, '_');
        // Preserve expanded state: use saved state, or auto-expand single item
        const isExpanded = expandedBoards.has(boardIdSafe) || (shouldAutoExpand && index === 0);
        const controlsDisabled = noHardware || !triggersData.enabled;
        const testButtonsDisabled = controlsDisabled || !board.isConnected;

        const boardStatusClass = board.isConnected ? 'text-success' : 'text-danger';
        const boardStatusText = board.isConnected ? 'Connected' : 'Disconnected';
        const portWarning = board.isPortBased
            ? '<span class="badge bg-warning text-dark ms-1" title="Identified by USB port - may change if moved"><i class="fas fa-exclamation-triangle"></i> Port-based</span>'
            : '';

        const channelsHtml = board.triggers.map(trigger => {
            const isOn = trigger.relayState === 'On';
            const activeStatus = isOn
                ? '<span class="badge bg-success ms-1">On</span>'
                : '<span class="badge bg-secondary ms-1">Off</span>';
            const onBtnClass = isOn ? 'btn btn-success btn-sm' : 'btn btn-outline-secondary btn-sm';
            const offBtnClass = !isOn && trigger.relayState === 'Off' ? 'btn btn-secondary btn-sm' : 'btn btn-outline-secondary btn-sm';

            return `
                <tr>
                    <td>
                        <span class="badge bg-primary">CH ${trigger.channel}</span>
                        ${activeStatus}
                    </td>
                    <td>
                        <select class="form-select form-select-sm"
                                id="trigger-sink-${boardIdSafe}-${trigger.channel}"
                                onchange="updateTriggerSink('${boardId}', ${trigger.channel}, this.value)"
                                ${controlsDisabled ? 'disabled' : ''}>
                            <option value="">Not assigned</option>
                            ${sinkOptions}
                        </select>
                    </td>
                    <td>
                        <div class="input-group input-group-sm">
                            <input type="number" class="form-control"
                                   id="trigger-delay-${boardIdSafe}-${trigger.channel}"
                                   value="${trigger.offDelaySeconds}"
                                   min="0" max="3600" step="1"
                                   onchange="updateTriggerDelay('${boardId}', ${trigger.channel}, this.value)"
                                   ${controlsDisabled ? 'disabled' : ''}>
                            <span class="input-group-text">s</span>
                        </div>
                    </td>
                    <td class="text-end" style="white-space: nowrap;">
                        <button class="${onBtnClass}"
                                onclick="testTrigger('${boardId}', ${trigger.channel}, true)"
                                title="Turn relay ON"
                                ${testButtonsDisabled ? 'disabled' : ''}>
                            <i class="fas fa-power-off"></i>
                        </button>
                        <button class="${offBtnClass}"
                                onclick="testTrigger('${boardId}', ${trigger.channel}, false)"
                                title="Turn relay OFF"
                                ${testButtonsDisabled ? 'disabled' : ''}>
                            <i class="fas fa-power-off"></i>
                        </button>
                    </td>
                </tr>
            `;
        }).join('');

        // Board type badge
        const boardTypeLabel = board.boardType === 'Ftdi' ? 'FTDI' : (board.boardType === 'UsbHid' ? 'HID' : board.boardType);
        const boardTypeBadge = board.boardType
            ? `<span class="badge ${board.boardType === 'Ftdi' ? 'bg-primary' : 'bg-info'} ms-2">${boardTypeLabel}</span>`
            : '';

        return `
            <div class="accordion-item">
                <h2 class="accordion-header">
                    <button class="accordion-button ${isExpanded ? '' : 'collapsed'}" type="button"
                            data-bs-toggle="collapse" data-bs-target="#board-${boardIdSafe}">
                        <span class="me-2"><i class="fas fa-microchip"></i></span>
                        <span class="fw-bold">${escapeHtml(board.displayName || board.boardId)}</span>
                        ${boardTypeBadge}
                        <span class="ms-2 small ${boardStatusClass}">${boardStatusText}</span>
                        ${portWarning}
                        <span class="ms-auto me-3 small text-muted">${board.channelCount} channels</span>
                    </button>
                </h2>
                <div id="board-${boardIdSafe}" class="accordion-collapse collapse ${isExpanded ? 'show' : ''}"
                     data-bs-parent="#triggersContainer">
                    <div class="accordion-body">
                        <div class="d-flex justify-content-between align-items-center mb-2">
                            <div class="small text-muted">
                                <i class="fas fa-fingerprint me-1"></i> ID: ${boardId}
                                ${board.usbPath ? `<span class="ms-2"><i class="fas fa-usb me-1"></i> Port: ${escapeHtml(board.usbPath)}</span>` : ''}
                            </div>
                            <div>
                                <button class="btn btn-outline-secondary btn-sm me-1" onclick="reconnectBoard('${boardId}')" title="Reconnect">
                                    <i class="fas fa-sync-alt"></i>
                                </button>
                                <button class="btn btn-outline-secondary btn-sm me-1" onclick="editBoard('${boardId}')" title="Edit">
                                    <i class="fas fa-cog"></i>
                                </button>
                                <button class="btn btn-outline-danger btn-sm" onclick="removeBoard('${boardId}')" title="Remove">
                                    <i class="fas fa-trash"></i>
                                </button>
                            </div>
                        </div>
                        <div class="d-flex align-items-center mb-2 small flex-wrap gap-2">
                            <div class="d-flex align-items-center">
                                <label class="text-muted me-2" for="startup-${boardIdSafe}">
                                    <i class="fas fa-power-off me-1"></i>Startup:
                                </label>
                                <select id="startup-${boardIdSafe}" class="form-select form-select-sm" style="width: auto;"
                                        onchange="updateBoardBehavior('${boardId}', 'startupBehavior', this.value)">
                                    <option value="AllOff" ${board.startupBehavior === 'AllOff' ? 'selected' : ''}>All OFF</option>
                                    <option value="AllOn" ${board.startupBehavior === 'AllOn' ? 'selected' : ''}>All ON</option>
                                    <option value="NoChange" ${board.startupBehavior === 'NoChange' ? 'selected' : ''}>No Change</option>
                                </select>
                            </div>
                            <div class="d-flex align-items-center">
                                <label class="text-muted me-2" for="shutdown-${boardIdSafe}">
                                    <i class="fas fa-stop-circle me-1"></i>Shutdown:
                                </label>
                                <select id="shutdown-${boardIdSafe}" class="form-select form-select-sm" style="width: auto;"
                                        onchange="updateBoardBehavior('${boardId}', 'shutdownBehavior', this.value)">
                                    <option value="AllOff" ${board.shutdownBehavior === 'AllOff' ? 'selected' : ''}>All OFF</option>
                                    <option value="AllOn" ${board.shutdownBehavior === 'AllOn' ? 'selected' : ''}>All ON</option>
                                    <option value="NoChange" ${board.shutdownBehavior === 'NoChange' ? 'selected' : ''}>No Change</option>
                                </select>
                            </div>
                            <span class="text-muted" title="Startup: when service starts. Shutdown: when service stops gracefully.">
                                <i class="fas fa-question-circle"></i>
                            </span>
                        </div>
                        ${board.errorMessage ? `<div class="alert alert-warning small mb-2"><i class="fas fa-exclamation-triangle me-1"></i>${escapeHtml(board.errorMessage)}</div>` : ''}
                        <table class="table table-sm table-hover mb-0">
                            <thead>
                                <tr>
                                    <th>Channel</th>
                                    <th>Sink</th>
                                    <th>Off Delay</th>
                                    <th class="text-end" style="white-space: nowrap;"><span style="display: inline-block; width: 38px; text-align: center;">On</span><span style="display: inline-block; width: 38px; text-align: center;">Off</span></th>
                                </tr>
                            </thead>
                            <tbody>
                                ${channelsHtml}
                            </tbody>
                        </table>
                    </div>
                </div>
            </div>
        `;
    }).join('');

    container.innerHTML = accordionHtml;

    // Set selected values for sink dropdowns
    triggersData.boards.forEach(board => {
        const boardIdSafe = board.boardId.replace(/[^a-zA-Z0-9]/g, '_');
        board.triggers.forEach(trigger => {
            const select = document.getElementById(`trigger-sink-${boardIdSafe}-${trigger.channel}`);
            if (select && trigger.customSinkName) {
                select.value = trigger.customSinkName;
            }
        });
    });

    // Restore scroll position after DOM update
    if (modalBody && scrollTop > 0) {
        requestAnimationFrame(() => {
            modalBody.scrollTop = scrollTop;
        });
    }
}

// Toggle triggers enabled
async function toggleTriggersEnabled(enabled) {
    try {
        const response = await fetch('./api/triggers/enabled', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to update triggers');
        }

        triggersData = await response.json();
        renderTriggers();
        showAlert(`12V triggers ${enabled ? 'enabled' : 'disabled'}`, 'success');
    } catch (error) {
        console.error('Error toggling triggers:', error);
        showAlert(`Failed to ${enabled ? 'enable' : 'disable'} triggers: ${error.message}`, 'danger');
        document.getElementById('triggersEnabled').checked = !enabled;
    }
}

// Show add board dialog
async function showAddBoardDialog() {
    if (!addBoardModal) {
        addBoardModal = new bootstrap.Modal(document.getElementById('addBoardModal'));
    }

    // Populate device selector
    const select = document.getElementById('addBoardDeviceSelect');
    select.innerHTML = '<option value="">Loading devices...</option>';

    try {
        const response = await fetch('./api/triggers/devices/all');
        const data = await response.json();
        ftdiDevicesData = data;

        // Filter out already-configured devices by boardId
        const configuredIds = triggersData?.boards?.map(b => b.boardId) || [];
        const availableDevices = data.devices.filter(d => !configuredIds.includes(d.boardId));

        if (availableDevices.length === 0) {
            select.innerHTML = '<option value="">No available devices</option>';
        } else {
            select.innerHTML = '<option value="">Select a device...</option>' +
                availableDevices.map(d => {
                    // Use boardId as the value (consistent with RelayDeviceInfo)
                    const id = d.boardId;
                    // Build label showing type
                    const typeLabel = d.boardType === 'UsbHid' ? 'HID' : 'FTDI';
                    // For FTDI boards, don't show channel count in label (user will select model)
                    const isFtdi = d.boardType === 'Ftdi';
                    const channelPart = !isFtdi && d.channelCount ? ` - ${d.channelCount}ch` : '';
                    const detectedNote = !isFtdi && d.channelCountDetected ? ' (auto)' : '';
                    // Always show USB port for differentiation, plus serial if available
                    const usbPort = extractUsbPort(d.usbPath);
                    const portPart = usbPort || '';
                    const serialPart = d.serialNumber ? `SN: ${d.serialNumber}` : '';
                    const idParts = [portPart, serialPart].filter(Boolean).join(', ');
                    const label = `[${typeLabel}] ${d.description || 'Relay Board'}${channelPart}${detectedNote}${idParts ? ` (${idParts})` : ''}`;
                    return `<option value="${escapeHtml(id)}" data-port-based="${d.isPathBased}" data-board-type="${d.boardType}" data-channel-count="${d.channelCount}" data-channel-detected="${d.channelCountDetected}">${escapeHtml(label)}</option>`;
                }).join('');
        }
    } catch (error) {
        select.innerHTML = '<option value="">Failed to load devices</option>';
    }

    const channelCountSelect = document.getElementById('addBoardChannelCount');
    const channelCountGroup = document.getElementById('addBoardChannelCountGroup');
    const ftdiModelGroup = document.getElementById('addBoardFtdiModelGroup');
    const ftdiModelSelect = document.getElementById('addBoardFtdiModel');

    // Update port warning visibility and channel count/model selector on selection change
    select.onchange = function() {
        const option = this.options[this.selectedIndex];
        const isPortBased = option?.dataset?.portBased === 'true';
        const channelDetected = option?.dataset?.channelDetected === 'true';
        const channelCount = option?.dataset?.channelCount;
        const boardType = option?.dataset?.boardType;
        const isFtdi = boardType === 'Ftdi';

        document.getElementById('addBoardPortWarning').classList.toggle('d-none', !isPortBased);

        // Show FTDI model selector for FTDI boards, channel count for others
        ftdiModelGroup.classList.toggle('d-none', !isFtdi);
        channelCountGroup.classList.toggle('d-none', isFtdi);

        if (isFtdi) {
            // For FTDI boards, default to 8-channel model
            ftdiModelSelect.value = 'Ro8';
        } else {
            // Auto-fill channel count from detected value for HID boards
            if (channelCount) {
                channelCountSelect.value = channelCount;
            }

            // Disable channel count selector if auto-detected
            channelCountSelect.disabled = channelDetected;
            if (channelDetected) {
                channelCountGroup.title = 'Channel count auto-detected from device';
            } else {
                channelCountGroup.title = '';
            }
        }
    };

    document.getElementById('addBoardDisplayName').value = '';
    channelCountSelect.value = '8';
    channelCountSelect.disabled = false;
    channelCountGroup.title = '';
    ftdiModelSelect.value = 'Ro8';
    ftdiModelGroup.classList.add('d-none');
    channelCountGroup.classList.remove('d-none');
    document.getElementById('addBoardPortWarning').classList.add('d-none');

    addBoardModal.show();
}

// Add a new board
async function addBoard() {
    const select = document.getElementById('addBoardDeviceSelect');
    const boardId = select.value;
    const displayName = document.getElementById('addBoardDisplayName').value.trim();

    // Get board type from selected option
    const selectedOption = select.options[select.selectedIndex];
    const boardType = selectedOption?.dataset?.boardType || 'Unknown';
    const isFtdi = boardType === 'Ftdi';

    // Get channel count from appropriate selector based on board type
    let channelCount;
    if (isFtdi) {
        // For FTDI boards, get channel count from model selector
        const ftdiModelSelect = document.getElementById('addBoardFtdiModel');
        const selectedModel = ftdiModelSelect.options[ftdiModelSelect.selectedIndex];
        channelCount = parseInt(selectedModel.dataset.channels, 10);
    } else {
        // For HID and other boards, use the channel count selector
        channelCount = parseInt(document.getElementById('addBoardChannelCount').value, 10);
    }

    if (!boardId) {
        showAlert('Please select a device', 'danger');
        return;
    }

    try {
        const response = await fetch('./api/triggers/boards', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ boardId, displayName: displayName || null, channelCount, boardType })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to add board');
        }

        addBoardModal.hide();
        await loadTriggers();
        showAlert('Relay board added successfully', 'success');
    } catch (error) {
        console.error('Error adding board:', error);
        showAlert(`Failed to add board: ${error.message}`, 'danger');
    }
}

// Remove a board
async function removeBoard(boardId) {
    const confirmed = await showConfirm(
        'Remove Relay Board',
        `Remove relay board "${boardId}"? This will delete all trigger configurations for this board.`,
        'Remove',
        'btn-danger'
    );

    if (!confirmed) {
        return;
    }

    // Prevent refresh interval from clobbering expanded state during this operation
    triggersOperationCount++;

    // Save expanded state before any async operations
    const container = document.getElementById('triggersContainer');
    container.querySelectorAll('.accordion-collapse.show').forEach(el => {
        const match = el.id.match(/^board-(.+)$/);
        if (match) {
            expandedBoardsState.add(match[1]);
        }
    });

    try {
        const response = await fetch(`./api/triggers/boards/${encodeURIComponent(boardId)}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to remove board');
        }

        await loadTriggers();
        showAlert('Relay board removed', 'success');
    } catch (error) {
        console.error('Error removing board:', error);
        showAlert(`Failed to remove board: ${error.message}`, 'danger');
    } finally {
        triggersOperationCount--;
    }
}

// Edit board settings (channel count, display name)
async function editBoard(boardId) {
    const board = triggersData?.boards?.find(b => b.boardId === boardId);
    if (!board) return;

    // Check board type
    const isFtdi = board.boardType === 'Ftdi';

    // Check if this device has auto-detected channel count (only relevant for non-FTDI)
    // Look up the device in the detected devices list
    let channelCountDetected = false;
    if (!isFtdi && ftdiDevicesData?.devices) {
        const device = ftdiDevicesData.devices.find(d => {
            // Match by board ID (could be serial-based or path-based)
            const deviceBoardId = d.boardId ||
                (d.serialNumber ? `HID:${d.serialNumber}` : null) ||
                (d.serialNumber ? d.serialNumber : null);
            return deviceBoardId === boardId ||
                   (d.serialNumber && boardId.includes(d.serialNumber));
        });
        if (device) {
            channelCountDetected = device.channelCountDetected === true;
        }
    }

    // Show combined edit modal
    const modal = document.getElementById('editBoardModal');
    const displayNameInput = document.getElementById('editBoardDisplayName');
    const channelCountSelect = document.getElementById('editBoardChannelCount');
    const channelCountGroup = document.getElementById('editBoardChannelCountGroup');
    const channelCountHelp = document.getElementById('editBoardChannelCountHelp');
    const ftdiModelGroup = document.getElementById('editBoardFtdiModelGroup');
    const ftdiModelSelect = document.getElementById('editBoardFtdiModel');
    const boardIdInput = document.getElementById('editBoardId');
    const boardTypeInput = document.getElementById('editBoardType');
    const saveBtn = document.getElementById('editBoardSaveBtn');

    // Populate form
    boardIdInput.value = boardId;
    boardTypeInput.value = board.boardType || '';
    displayNameInput.value = board.displayName || '';

    // Show appropriate selector based on board type
    if (isFtdi) {
        // For FTDI boards, show model selector
        ftdiModelGroup.classList.remove('d-none');
        channelCountGroup.classList.add('d-none');
        // Set model based on current channel count (4 = Ro4, 8 = Ro8 or Generic8)
        ftdiModelSelect.value = board.channelCount === 4 ? 'Ro4' : 'Ro8';
    } else {
        // For HID and other boards, show channel count selector
        ftdiModelGroup.classList.add('d-none');
        channelCountGroup.classList.remove('d-none');
        channelCountSelect.value = String(board.channelCount);
        // Disable channel count if auto-detected
        channelCountSelect.disabled = channelCountDetected;
        channelCountHelp.classList.toggle('d-none', !channelCountDetected);
    }

    const bsModal = new bootstrap.Modal(modal);

    // Clean up any previous handlers by cloning the button
    const newSaveBtn = saveBtn.cloneNode(true);
    saveBtn.parentNode.replaceChild(newSaveBtn, saveBtn);

    newSaveBtn.addEventListener('click', async () => {
        const newName = displayNameInput.value.trim();

        // Get channel count from appropriate selector based on board type
        let newCount;
        if (isFtdi) {
            const selectedModel = ftdiModelSelect.options[ftdiModelSelect.selectedIndex];
            newCount = parseInt(selectedModel.dataset.channels, 10);
        } else {
            newCount = parseInt(channelCountSelect.value, 10);
        }

        try {
            const response = await fetch(`./api/triggers/boards/${encodeURIComponent(boardId)}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    displayName: newName || null,
                    channelCount: channelCountDetected ? null : newCount
                })
            });

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.message || 'Failed to update board');
            }

            bsModal.hide();
            await loadTriggers();
            showAlert('Board settings updated', 'success');
        } catch (error) {
            console.error('Error updating board:', error);
            showAlert(`Failed to update board: ${error.message}`, 'danger');
        }
    });

    bsModal.show();

    // Focus display name input when modal is shown
    modal.addEventListener('shown.bs.modal', function focusHandler() {
        modal.removeEventListener('shown.bs.modal', focusHandler);
        displayNameInput.focus();
        displayNameInput.select();
    });
}

// Update board startup or shutdown behavior
async function updateBoardBehavior(boardId, behaviorType, value) {
    const isStartup = behaviorType === 'startupBehavior';
    const typeName = isStartup ? 'Startup' : 'Shutdown';

    // Prevent refresh interval from clobbering expanded state during this operation
    triggersOperationCount++;

    // Save expanded state before any async operations
    const container = document.getElementById('triggersContainer');
    container.querySelectorAll('.accordion-collapse.show').forEach(el => {
        const match = el.id.match(/^board-(.+)$/);
        if (match) {
            expandedBoardsState.add(match[1]);
        }
    });

    try {
        const response = await fetch(`./api/triggers/boards/${encodeURIComponent(boardId)}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ [behaviorType]: value })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || `Failed to update ${typeName.toLowerCase()} behavior`);
        }

        // Update local data
        const board = triggersData?.boards?.find(b => b.boardId === boardId);
        if (board) {
            board[behaviorType] = value;
        }

        const label = value === 'AllOff' ? 'All OFF' :
                      value === 'AllOn' ? 'All ON' : 'No Change';
        showAlert(`${typeName} behavior set to "${label}"`, 'success');
    } catch (error) {
        console.error(`Error updating ${typeName.toLowerCase()} behavior:`, error);
        showAlert(`Failed to update ${typeName.toLowerCase()} behavior: ${error.message}`, 'danger');
        // Revert the dropdown by reloading
        await loadTriggers();
    } finally {
        triggersOperationCount--;
    }
}

// Reconnect a specific board
async function reconnectBoard(boardId) {
    // Prevent refresh interval from clobbering expanded state during this operation
    triggersOperationCount++;

    // Save expanded state before any async operations
    const container = document.getElementById('triggersContainer');
    container.querySelectorAll('.accordion-collapse.show').forEach(el => {
        const match = el.id.match(/^board-(.+)$/);
        if (match) {
            expandedBoardsState.add(match[1]);
        }
    });

    try {
        const response = await fetch(`./api/triggers/boards/${encodeURIComponent(boardId)}/reconnect`, {
            method: 'POST'
        });

        const boardStatus = await response.json();
        await loadTriggers();

        if (boardStatus.isConnected) {
            showAlert(`Board "${boardId}" reconnected`, 'success');
        } else {
            showAlert(`Reconnection failed: ${boardStatus.errorMessage || 'Unknown error'}`, 'warning');
        }
    } catch (error) {
        console.error('Error reconnecting board:', error);
        showAlert(`Failed to reconnect: ${error.message}`, 'danger');
    } finally {
        triggersOperationCount--;
    }
}

// Update trigger sink assignment (multi-board)
async function updateTriggerSink(boardId, channel, sinkName) {
    const boardIdSafe = boardId.replace(/[^a-zA-Z0-9]/g, '_');
    const delayInput = document.getElementById(`trigger-delay-${boardIdSafe}-${channel}`);
    const delay = delayInput ? parseInt(delayInput.value, 10) : 60;

    try {
        const response = await fetch(`./api/triggers/boards/${encodeURIComponent(boardId)}/${channel}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                channel,
                customSinkName: sinkName || null,
                offDelaySeconds: delay
            })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to update trigger');
        }

        showAlert(`Trigger updated`, 'success', 2000);
    } catch (error) {
        console.error('Error updating trigger:', error);
        showAlert(`Failed to update trigger: ${error.message}`, 'danger');
    }
}

// Update trigger off delay (multi-board)
async function updateTriggerDelay(boardId, channel, delay) {
    const boardIdSafe = boardId.replace(/[^a-zA-Z0-9]/g, '_');
    const sinkSelect = document.getElementById(`trigger-sink-${boardIdSafe}-${channel}`);
    const sinkName = sinkSelect ? sinkSelect.value : null;

    try {
        const response = await fetch(`./api/triggers/boards/${encodeURIComponent(boardId)}/${channel}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                channel,
                customSinkName: sinkName || null,
                offDelaySeconds: parseInt(delay, 10)
            })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to update trigger');
        }

        showAlert(`Delay updated`, 'success', 2000);
    } catch (error) {
        console.error('Error updating trigger delay:', error);
        showAlert(`Failed to update trigger: ${error.message}`, 'danger');
    }
}

// Update a specific channel's UI state without reloading everything
function updateChannelState(boardId, channel, isOn) {
    const boardIdSafe = boardId.replace(/[^a-zA-Z0-9]/g, '_');

    // Update local triggersData
    const board = triggersData?.boards?.find(b => b.boardId === boardId);
    if (board) {
        const trigger = board.triggers?.find(t => t.channel === channel);
        if (trigger) {
            trigger.relayState = isOn ? 'On' : 'Off';
        }
    }

    // Find the row for this channel by looking for the sink select element
    const sinkSelect = document.getElementById(`trigger-sink-${boardIdSafe}-${channel}`);
    if (!sinkSelect) return;

    const row = sinkSelect.closest('tr');
    if (!row) return;

    // Update the badge in the first cell
    const firstCell = row.cells[0];
    const badge = firstCell.querySelector('.badge.bg-success, .badge.bg-secondary');
    if (badge) {
        if (isOn) {
            badge.className = 'badge bg-success ms-1';
            badge.textContent = 'On';
        } else {
            badge.className = 'badge bg-secondary ms-1';
            badge.textContent = 'Off';
        }
    }

    // Update the button classes in the last cell
    const lastCell = row.cells[row.cells.length - 1];
    const buttons = lastCell.querySelectorAll('button');
    if (buttons.length === 2) {
        const onBtn = buttons[0];
        const offBtn = buttons[1];

        if (isOn) {
            onBtn.className = 'btn btn-success btn-sm';
            offBtn.className = 'btn btn-outline-secondary btn-sm';
        } else {
            onBtn.className = 'btn btn-outline-secondary btn-sm';
            offBtn.className = 'btn btn-secondary btn-sm';
        }
    }
}

// Test a trigger relay (multi-board)
async function testTrigger(boardId, channel, on) {
    // Prevent refresh interval from clobbering expanded state during this operation
    triggersOperationCount++;

    try {
        // Use query params for board IDs that contain slashes (e.g., MODBUS:/dev/ttyUSB0)
        const url = boardId.includes('/')
            ? `./api/triggers/boards/test?boardId=${encodeURIComponent(boardId)}&channel=${channel}`
            : `./api/triggers/boards/${encodeURIComponent(boardId)}/${channel}/test`;
        const response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ on })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to test relay');
        }

        showAlert(`Relay ${channel} turned ${on ? 'ON' : 'OFF'}`, 'success', 2000);

        // Update just the affected channel's UI instead of reloading everything
        updateChannelState(boardId, channel, on);
    } catch (error) {
        console.error('Error testing trigger:', error);
        showAlert(`Failed to test relay: ${error.message}`, 'danger');
    } finally {
        triggersOperationCount--;
    }
}

// Reconnect all boards (legacy function for compatibility)
async function reconnectTriggerBoard() {
    try {
        const response = await fetch('./api/triggers/reconnect', {
            method: 'POST'
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to reconnect');
        }

        triggersData = await response.json();
        renderTriggers();

        const connectedCount = triggersData.boards.filter(b => b.isConnected).length;
        if (connectedCount === triggersData.boards.length) {
            showAlert('All boards reconnected', 'success');
        } else {
            showAlert(`Reconnected ${connectedCount}/${triggersData.boards.length} boards`, 'warning');
        }
    } catch (error) {
        console.error('Error reconnecting:', error);
        showAlert(`Failed to reconnect: ${error.message}`, 'danger');
    }
}
