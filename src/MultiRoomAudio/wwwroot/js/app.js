// Multi-Room Audio Controller - JavaScript App

// State
let players = {};
let devices = [];
let connection = null;

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
        buildVersion.textContent = formatBuildVersion(data);
        buildVersion.title = data?.build ? `Full build: ${data.build}` : '';
    } catch (error) {
        console.error('Error fetching build info:', error);
        buildVersion.textContent = 'unknown';
        buildVersion.title = '';
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

// XSS protection
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Format sample rate for display (e.g., 48000 -> "48kHz", 192000 -> "192kHz")
function formatSampleRate(rate) {
    if (rate >= 1000) {
        return (rate / 1000) + 'kHz';
    }
    return rate + 'Hz';
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

// Initialize app
document.addEventListener('DOMContentLoaded', async () => {
    // Load initial data
    await Promise.all([
        refreshBuildInfo(),
        refreshStatus(),
        refreshDevices()
    ]);

    // Set up volume slider preview
    const volumeSlider = document.getElementById('initialVolume');
    const volumeValue = document.getElementById('initialVolumeValue');
    if (volumeSlider && volumeValue) {
        volumeSlider.addEventListener('input', () => {
            volumeValue.textContent = volumeSlider.value + '%';
        });
    }

    // Set up SignalR connection
    setupSignalR();

    // Check if onboarding wizard should show on fresh install
    if (typeof Wizard !== 'undefined') {
        const shouldShow = await Wizard.shouldShow();
        if (shouldShow) {
            await Wizard.show();
        }
    }

    // Poll for status updates as fallback
    setInterval(refreshStatus, 5000);
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
        .withAutomaticReconnect()
        .build();

    connection.on('PlayerStatusUpdate', (data) => {
        console.log('Status update:', data);
        if (data.players) {
            // Convert array to object keyed by name (same as refreshStatus)
            players = {};
            (data.players || []).forEach(p => {
                players[p.name] = p;
            });
            renderPlayers();
        }
    });

    connection.onreconnecting(() => {
        statusBadge.textContent = 'Reconnecting...';
        statusBadge.className = 'badge bg-warning me-2';
    });

    connection.onreconnected(() => {
        statusBadge.textContent = 'Connected';
        statusBadge.className = 'badge bg-success me-2';
    });

    connection.onclose(() => {
        statusBadge.textContent = 'Disconnected';
        statusBadge.className = 'badge bg-danger me-2';
    });

    connection.start()
        .then(() => {
            statusBadge.textContent = 'Connected';
            statusBadge.className = 'badge bg-success me-2';
        })
        .catch(err => {
            console.log('SignalR connection failed, using polling:', err);
            statusBadge.textContent = 'Polling';
            statusBadge.className = 'badge bg-info me-2';
        });
}

// API calls
async function refreshStatus() {
    try {
        const response = await fetch('./api/players');
        if (!response.ok) throw new Error('Failed to fetch players');

        const data = await response.json();
        players = {};
        (data.players || []).forEach(p => {
            players[p.name] = p;
        });

        renderPlayers();
    } catch (error) {
        console.error('Error refreshing status:', error);
        showAlert('Failed to load players', 'danger');
    }
}

async function refreshDevices() {
    try {
        const response = await fetch('./api/devices');
        if (!response.ok) throw new Error('Failed to fetch devices');

        const data = await response.json();
        devices = data.devices || [];

        // Update device selects
        const selects = document.querySelectorAll('#audioDevice, #editAudioDevice');
        selects.forEach(select => {
            const currentValue = select.value;
            select.innerHTML = '<option value="">Default Device</option>';
            devices.forEach(device => {
                const option = document.createElement('option');
                option.value = device.id;
                option.textContent = `${device.alias || device.name}${device.isDefault ? ' (default)' : ''}`;
                select.appendChild(option);
            });
            if (currentValue) select.value = currentValue;
        });
    } catch (error) {
        console.error('Error refreshing devices:', error);
    }
}

// Open the modal in Add mode
function openAddPlayerModal() {
    // Reset form
    document.getElementById('playerForm').reset();
    document.getElementById('editingPlayerName').value = '';
    document.getElementById('initialVolumeValue').textContent = '75%';

    // Set modal to Add mode
    document.getElementById('playerModalIcon').className = 'fas fa-plus-circle me-2';
    document.getElementById('playerModalTitleText').textContent = 'Add New Player';
    document.getElementById('playerModalSubmitIcon').className = 'fas fa-plus me-1';
    document.getElementById('playerModalSubmitText').textContent = 'Add Player';

    // Refresh devices and show modal
    refreshDevices();
    const modal = new bootstrap.Modal(document.getElementById('playerModal'));
    modal.show();
}

// Open the modal in Edit mode with player data
async function openEditPlayerModal(playerName) {
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
        document.getElementById('initialVolume').value = player.volume;
        document.getElementById('initialVolumeValue').textContent = player.volume + '%';

        // Set device dropdown
        await refreshDevices();
        const audioDeviceSelect = document.getElementById('audioDevice');
        if (player.device) {
            audioDeviceSelect.value = player.device;
            // Check if the device was actually found in the list
            if (audioDeviceSelect.value !== player.device) {
                showAlert(`Warning: Previously configured device "${player.device}" is no longer available. Please select a new device.`, 'warning');
            }
        }

        // Set modal to Edit mode
        document.getElementById('playerModalIcon').className = 'fas fa-edit me-2';
        document.getElementById('playerModalTitleText').textContent = 'Edit Player';
        document.getElementById('playerModalSubmitIcon').className = 'fas fa-save me-1';
        document.getElementById('playerModalSubmitText').textContent = 'Save Changes';

        const modal = new bootstrap.Modal(document.getElementById('playerModal'));
        modal.show();
    } catch (error) {
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

    // Disable submit button to prevent double-click
    const submitBtn = document.getElementById('playerModalSubmit');
    submitBtn.disabled = true;

    try {
        if (isEditing) {
            // Edit mode: Use PUT to update config, then restart if needed
            const updatePayload = {
                name: name !== editingName ? name : undefined,  // Only include if changed
                device: device || '',  // Empty string = default device
                serverUrl: serverUrl || '',  // Empty string = mDNS discovery
                volume
            };

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

            // Close modal and refresh
            bootstrap.Modal.getInstance(document.getElementById('playerModal')).hide();
            document.getElementById('playerForm').reset();
            document.getElementById('initialVolumeValue').textContent = '75%';
            await refreshStatus();

            // Show appropriate message based on changes
            if (result.needsRestart) {
                if (wasRenamed) {
                    // For renames, offer to restart rather than auto-restart
                    // The name change is saved locally, but Music Assistant needs a restart to see it
                    showAlert(
                        `Player renamed to "${finalName}". Restart the player for the name to appear in Music Assistant.`,
                        'info',
                        8000 // Show for longer since it's actionable
                    );
                } else {
                    // For other changes requiring restart (e.g., server URL), auto-restart
                    const restartResponse = await fetch(`./api/players/${encodeURIComponent(finalName)}/restart`, {
                        method: 'POST'
                    });
                    if (!restartResponse.ok) {
                        console.warn('Restart request failed, player may need manual restart');
                    }
                    showAlert(`Player "${finalName}" updated and restarted`, 'success');
                }
            } else {
                showAlert(`Player "${finalName}" updated successfully`, 'success');
            }
        } else {
            // Add mode: Create new player
            const response = await fetch('./api/players', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    name,
                    device: device || null,
                    serverUrl: serverUrl || null,
                    volume,
                    persist: true
                })
            });

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.error || 'Failed to create player');
            }

            // Close modal and refresh
            bootstrap.Modal.getInstance(document.getElementById('playerModal')).hide();
            document.getElementById('playerForm').reset();
            document.getElementById('initialVolumeValue').textContent = '75%';
            await refreshStatus();

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
    if (!confirm(`Delete player "${name}"? This will also remove its configuration.`)) {
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
    if (!confirm(`Stop player "${name}"? This will disconnect it from the server.`)) {
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
    } catch (error) {
        console.error('Error setting volume:', error);
        showAlert(error.message, 'danger');
    }
}

/**
 * Set the hardware volume limit for a player's audio device.
 * This controls the PulseAudio sink volume (physical output level).
 */
async function setHardwareVolumeLimit(name, maxVolume) {
    try {
        const response = await fetch(`./api/players/${encodeURIComponent(name)}/hardware-volume`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ maxVolume: parseInt(maxVolume) })
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Failed to set hardware volume');
        }

        // Update local state
        if (players[name]) {
            players[name].hardwareVolumeLimit = parseInt(maxVolume);
        }

        showAlert(`Hardware volume set to ${maxVolume}%`, 'success', 2000);
    } catch (error) {
        console.error('Error setting hardware volume:', error);
        showAlert(error.message, 'danger');
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

    body.innerHTML = `
        <div class="row">
            <div class="col-md-6">
                <h6 class="text-muted text-uppercase small">Configuration</h6>
                <table class="table table-sm">
                    <tr><td><strong>Name</strong></td><td>${escapeHtml(player.name)}</td></tr>
                    <tr><td><strong>Device</strong></td><td>${escapeHtml(getDeviceDisplayName(player.device))}</td></tr>
                    <tr><td><strong>Client ID</strong></td><td><code>${escapeHtml(player.clientId)}</code></td></tr>
                    <tr><td><strong>Server</strong></td><td>${escapeHtml(player.serverUrl || 'Auto-discovered')}</td></tr>
                    <tr><td><strong>Volume</strong></td><td>${player.volume}%</td></tr>
                </table>
            </div>
            <div class="col-md-6">
                <h6 class="text-muted text-uppercase small">Status</h6>
                <table class="table table-sm">
                    <tr><td><strong>State</strong></td><td><span class="badge bg-${getStateBadgeClass(player.state)}">${player.state}</span></td></tr>
                    <tr><td><strong>Clock Synced</strong></td><td>${player.isClockSynced ? '<i class="fas fa-check text-success"></i> Yes' : '<i class="fas fa-times text-danger"></i> No'}</td></tr>
                    <tr><td><strong>Muted</strong></td><td>${player.isMuted ? 'Yes' : 'No'}</td></tr>
                    <tr><td><strong>Output Latency</strong></td><td>${player.outputLatencyMs}ms</td></tr>
                    <tr><td><strong>Created</strong></td><td>${new Date(player.createdAt).toLocaleString()}</td></tr>
                    ${player.connectedAt ? `<tr><td><strong>Connected</strong></td><td>${new Date(player.connectedAt).toLocaleString()}</td></tr>` : ''}
                    ${player.errorMessage ? `<tr><td><strong>Error</strong></td><td class="text-danger">${escapeHtml(player.errorMessage)}</td></tr>` : ''}
                </table>
            </div>
        </div>
        <div class="row mt-3">
            <div class="col-md-6">
                <h6 class="text-muted text-uppercase small">Audio Output</h6>
                <table class="table table-sm">
                    <tr><td><strong>Format</strong></td><td>FLOAT32 (32-bit)</td></tr>
                    <tr><td><strong>Conversion</strong></td><td>PulseAudio (native)</td></tr>
                    <tr><td><strong>Latency</strong></td><td>${player.outputLatencyMs}ms</td></tr>
                </table>
            </div>
            <div class="col-md-6">
                <h6 class="text-muted text-uppercase small">Device Capabilities</h6>
                ${player.deviceCapabilities ? `
                <table class="table table-sm">
                    <tr><td><strong>Sample Rates</strong></td><td>${player.deviceCapabilities.supportedSampleRates.map(r => formatSampleRate(r)).join(', ')}</td></tr>
                    <tr><td><strong>Bit Depths</strong></td><td>${player.deviceCapabilities.supportedBitDepths.map(b => b + '-bit').join(', ')}</td></tr>
                    <tr><td><strong>Max Channels</strong></td><td>${player.deviceCapabilities.maxChannels}</td></tr>
                </table>
                ` : '<p class="text-muted small">Capability probing not available</p>'}
            </div>
        </div>
        <h6 class="text-muted text-uppercase small mt-3">Delay Offset</h6>
        <p class="text-muted small mb-2">
            <i class="fas fa-info-circle me-1"></i>
            Adjust timing to sync this player with others. Use <strong>positive</strong> values to delay playback
            (if this speaker plays too early), or <strong>negative</strong> values to advance it (if too late).
            The player will restart when you close this dialog to apply changes.
        </p>
        <div class="delay-control d-flex align-items-center gap-2 flex-wrap">
            <button class="btn btn-outline-secondary btn-sm" onclick="adjustDelay('${escapeHtml(name)}', -10)" title="Decrease by 10ms">
                <i class="fas fa-minus"></i>
            </button>
            <div class="input-group input-group-sm" style="max-width: 140px;">
                <input type="number" class="form-control text-center" id="delayInput"
                    value="${player.delayMs}" min="-5000" max="5000" step="10"
                    onchange="setDelay('${escapeHtml(name)}', this.value)"
                    onkeydown="if(event.key==='Enter'){setDelay('${escapeHtml(name)}', this.value); event.preventDefault();}">
                <span class="input-group-text">ms</span>
            </div>
            <button class="btn btn-outline-secondary btn-sm" onclick="adjustDelay('${escapeHtml(name)}', 10)" title="Increase by 10ms">
                <i class="fas fa-plus"></i>
            </button>
            <small class="text-muted">Range: -5000 to +5000ms</small>
            <span id="delaySavedIndicator" class="text-success small" style="opacity: 0; transition: opacity 0.3s;"><i class="fas fa-check"></i> Saved</span>
        </div>
        ${player.metrics ? `
        <h6 class="text-muted text-uppercase small mt-3">Metrics</h6>
        <div class="d-flex flex-wrap">
            <span class="metric-badge bg-body-secondary border"><i class="fas fa-database"></i> Buffer: ${player.metrics.bufferLevel}/${player.metrics.bufferCapacity}ms</span>
            <span class="metric-badge bg-body-secondary border"><i class="fas fa-music"></i> Samples: ${player.metrics.samplesPlayed.toLocaleString()}</span>
            <span class="metric-badge ${player.metrics.underruns > 0 ? 'bg-warning' : 'bg-body-secondary border'}"><i class="fas fa-exclamation-triangle"></i> Underruns: ${player.metrics.underruns}</span>
            <span class="metric-badge ${player.metrics.overruns > 0 ? 'bg-warning' : 'bg-body-secondary border'}"><i class="fas fa-level-up-alt"></i> Overruns: ${player.metrics.overruns}</span>
        </div>
        ` : ''}
    `;

    bootstrap.Modal.getOrCreateInstance(modal).show();
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
                                <button class="btn btn-sm btn-outline-info me-1" onclick="showPlayerStats('${escapeHtml(name)}')" title="Player Details">
                                    <i class="fas fa-info-circle"></i>
                                </button>
                                <button class="btn btn-sm btn-outline-secondary me-1" onclick="openStatsForNerds('${escapeHtml(name)}')" title="Stats for Nerds">
                                    <i class="fas fa-terminal"></i>
                                </button>
                                <div class="dropdown">
                                    <button class="btn btn-sm btn-outline-secondary" type="button" data-bs-toggle="dropdown">
                                        <i class="fas fa-ellipsis-v"></i>
                                    </button>
                                    <ul class="dropdown-menu">
                                        <li><a class="dropdown-item" href="#" onclick="openEditPlayerModal('${escapeHtml(name)}'); return false;"><i class="fas fa-edit me-2"></i>Edit</a></li>
                                        <li><a class="dropdown-item" href="#" onclick="restartPlayer('${escapeHtml(name)}'); return false;"><i class="fas fa-sync me-2"></i>Restart</a></li>
                                        <li><a class="dropdown-item" href="#" onclick="stopPlayer('${escapeHtml(name)}'); return false;"><i class="fas fa-stop me-2"></i>Stop</a></li>
                                        <li><hr class="dropdown-divider"></li>
                                        <li><a class="dropdown-item text-danger" href="#" onclick="deletePlayer('${escapeHtml(name)}'); return false;"><i class="fas fa-trash me-2"></i>Delete</a></li>
                                    </ul>
                                </div>
                            </div>
                        </div>

                        <div class="status-container mb-3">
                            <span class="status-indicator ${stateClass}"></span>
                            <span class="badge bg-${stateBadgeClass}">${player.state}</span>
                            <small class="text-muted ms-2">${escapeHtml(getDeviceDisplayName(player.device))}</small>
                        </div>

                        <div class="volume-control">
                            <div class="d-flex align-items-center">
                                <i class="fas fa-volume-down me-2"></i>
                                <input type="range" class="form-range flex-grow-1" min="0" max="100" value="${player.volume}"
                                    onchange="setVolume('${escapeHtml(name)}', this.value)"
                                    oninput="this.nextElementSibling.textContent = this.value + '%'">
                                <span class="volume-display ms-2">${player.volume}%</span>
                            </div>
                        </div>

                        <div class="hardware-volume-section mt-3 pt-2 border-top">
                            <div class="d-flex align-items-center mb-1">
                                <i class="fas fa-cog me-1 text-muted small"></i>
                                <span class="small text-muted">Hardware Volume</span>
                            </div>
                            <div class="d-flex align-items-center">
                                <input type="range" class="form-range form-range-sm flex-grow-1" min="0" max="100"
                                       value="${player.hardwareVolumeLimit || 80}"
                                       onchange="setHardwareVolumeLimit('${escapeHtml(name)}', this.value)"
                                       oninput="this.nextElementSibling.textContent = this.value + '%'">
                                <span class="volume-display ms-2 small">${player.hardwareVolumeLimit || 80}%</span>
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
}

function getStateClass(state) {
    const stateMap = {
        'Playing': 'status-playing',
        'Connected': 'status-connected',
        'Buffering': 'status-buffering',
        'Starting': 'status-starting',
        'Connecting': 'status-connecting',
        'Stopped': 'status-stopped',
        'Error': 'status-error'
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
        'Error': 'danger'
    };
    return stateMap[state] || 'secondary';
}

// Alert helpers
function showAlert(message, type = 'info', duration = 5000) {
    const container = document.getElementById('alert-container');
    const alert = document.createElement('div');
    alert.className = `alert alert-${type} alert-dismissible fade show`;
    alert.innerHTML = `
        ${escapeHtml(message)}
        <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
    `;
    container.appendChild(alert);

    // Auto-dismiss after specified duration
    setTimeout(() => {
        alert.classList.remove('show');
        setTimeout(() => alert.remove(), 150);
    }, duration);
}

// ========== Stats for Nerds ==========
let statsInterval = null;
let currentStatsPlayer = null;

function openStatsForNerds(playerName) {
    currentStatsPlayer = playerName;

    // Update player name in modal header
    const playerNameSpan = document.getElementById('statsPlayerName');
    if (playerNameSpan) {
        playerNameSpan.textContent = '• ' + playerName;
    }

    // Show modal
    const modal = new bootstrap.Modal(document.getElementById('statsForNerdsModal'));
    modal.show();

    // Start polling
    fetchAndRenderStats();
    statsInterval = setInterval(fetchAndRenderStats, 500);

    // Stop polling when modal closes
    document.getElementById('statsForNerdsModal').addEventListener('hidden.bs.modal', () => {
        clearInterval(statsInterval);
        statsInterval = null;
        currentStatsPlayer = null;
    }, { once: true });
}

async function fetchAndRenderStats() {
    if (!currentStatsPlayer) return;

    try {
        const response = await fetch(`./api/players/${encodeURIComponent(currentStatsPlayer)}/stats`);
        if (!response.ok) {
            throw new Error('Failed to fetch stats');
        }
        const stats = await response.json();
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
    }
}

function renderStatsPanel(stats) {
    const body = document.getElementById('statsForNerdsBody');

    body.innerHTML = `
        <!-- Audio Format Section -->
        <div class="stats-section">
            <div class="stats-section-header">Audio Format</div>
            <div class="stats-row">
                <span class="stats-label">Input</span>
                <span class="stats-value info">${escapeHtml(stats.audioFormat.inputFormat)}</span>
            </div>
            ${stats.audioFormat.inputBitrate ? `
            <div class="stats-row">
                <span class="stats-label">Bitrate</span>
                <span class="stats-value">${escapeHtml(stats.audioFormat.inputBitrate)}</span>
            </div>
            ` : ''}
            <div class="stats-row">
                <span class="stats-label">Output</span>
                <span class="stats-value info">${escapeHtml(stats.audioFormat.outputFormat)}</span>
            </div>
        </div>

        <!-- Sync Status Section -->
        <div class="stats-section">
            <div class="stats-section-header">Sync Status</div>
            <div class="stats-row">
                <span class="stats-label">Sync Error</span>
                <span class="stats-value ${getSyncErrorClass(stats.sync.syncErrorMs)}">${formatMs(stats.sync.syncErrorMs)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Status</span>
                <span class="stats-value ${stats.sync.isWithinTolerance ? 'good' : 'warning'}">${stats.sync.isWithinTolerance ? 'Within tolerance' : 'Correcting'}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Playback Active</span>
                <span class="stats-value ${stats.sync.isPlaybackActive ? 'good' : 'muted'}">${stats.sync.isPlaybackActive ? 'Yes' : 'No'}</span>
            </div>
        </div>

        <!-- Buffer Section -->
        <div class="stats-section">
            <div class="stats-section-header">Buffer</div>
            <div class="stats-row">
                <span class="stats-label">Buffered</span>
                <span class="stats-value">${stats.buffer.bufferedMs}ms / ${stats.buffer.targetMs}ms</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Underruns</span>
                <span class="stats-value ${stats.buffer.underruns > 0 ? 'bad' : 'good'}">${formatCount(stats.buffer.underruns)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Overruns</span>
                <span class="stats-value ${stats.buffer.overruns > 0 ? 'warning' : 'good'}">${formatCount(stats.buffer.overruns)}</span>
            </div>
        </div>

        <!-- Sync Correction Section -->
        <div class="stats-section">
            <div class="stats-section-header">Sync Correction</div>
            <div class="stats-row">
                <span class="stats-label">Mode</span>
                <span class="stats-value ${getCorrectionModeClass(stats.correction.mode)}">${escapeHtml(stats.correction.mode)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Threshold</span>
                <span class="stats-value">${stats.correction.thresholdMs}ms</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Frames Dropped</span>
                <span class="stats-value ${stats.correction.framesDropped > 0 ? 'warning' : ''}">${formatSampleCount(stats.correction.framesDropped)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Frames Inserted</span>
                <span class="stats-value ${stats.correction.framesInserted > 0 ? 'warning' : ''}">${formatSampleCount(stats.correction.framesInserted)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Dropped (Overflow)</span>
                <span class="stats-value ${stats.throughput.samplesDroppedOverflow > 0 ? 'bad' : ''}">${formatSampleCount(stats.throughput.samplesDroppedOverflow)}</span>
            </div>
        </div>

        <!-- Clock Sync Section -->
        <div class="stats-section">
            <div class="stats-section-header">Clock Sync</div>
            <div class="stats-row">
                <span class="stats-label">Status</span>
                <span class="stats-value">
                    <span class="sync-indicator">
                        <span class="sync-dot ${stats.clockSync.isSynchronized ? '' : (stats.clockSync.measurementCount > 0 ? 'syncing' : 'not-synced')}"></span>
                        <span class="${stats.clockSync.isSynchronized ? 'good' : 'warning'}">${stats.clockSync.isSynchronized ? 'Synchronized' : (stats.clockSync.measurementCount > 0 ? 'Syncing...' : 'Not synced')}</span>
                    </span>
                </span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Clock Offset</span>
                <span class="stats-value">${formatMs(stats.clockSync.clockOffsetMs)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Uncertainty</span>
                <span class="stats-value">${formatMs(stats.clockSync.uncertaintyMs)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Drift Rate</span>
                <span class="stats-value ${stats.clockSync.isDriftReliable ? '' : 'muted'}">${stats.clockSync.driftRatePpm.toFixed(1)} ppm ${stats.clockSync.isDriftReliable ? '' : '(unstable)'}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Measurements</span>
                <span class="stats-value">${formatCount(stats.clockSync.measurementCount)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Output Latency</span>
                <span class="stats-value">${stats.clockSync.outputLatencyMs}ms</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Static Delay</span>
                <span class="stats-value">${stats.clockSync.staticDelayMs}ms</span>
            </div>
        </div>

        <!-- Throughput Section -->
        <div class="stats-section">
            <div class="stats-section-header">Throughput</div>
            <div class="stats-row">
                <span class="stats-label">Samples Written</span>
                <span class="stats-value">${formatSampleCount(stats.throughput.samplesWritten)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Samples Read</span>
                <span class="stats-value">${formatSampleCount(stats.throughput.samplesRead)}</span>
            </div>
        </div>

        <!-- Buffer Diagnostics Section -->
        <div class="stats-section">
            <div class="stats-section-header">
                <i class="fas fa-stethoscope me-1"></i>Buffer Diagnostics
            </div>
            <div class="stats-row">
                <span class="stats-label">State</span>
                <span class="stats-value ${getBufferStateClass(stats.diagnostics.state)}">${escapeHtml(stats.diagnostics.state)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Fill Level</span>
                <span class="stats-value">${stats.diagnostics.fillPercent}%</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Pipeline State</span>
                <span class="stats-value info">${escapeHtml(stats.diagnostics.pipelineState)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Has Received Data</span>
                <span class="stats-value ${stats.diagnostics.hasReceivedSamples ? 'good' : 'warning'}">${stats.diagnostics.hasReceivedSamples ? 'Yes' : 'No'}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Dropped (Overflow)</span>
                <span class="stats-value ${stats.diagnostics.droppedOverflow > 0 ? 'bad' : 'good'}">${formatSampleCount(stats.diagnostics.droppedOverflow)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Smoothed Sync Error</span>
                <span class="stats-value">${formatUs(stats.diagnostics.smoothedSyncErrorUs)}</span>
            </div>
        </div>
    `;
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
                                        onclick="playTestToneForSink('${escapeHtml(name)}')"
                                        title="Play test tone"
                                        ${!isLoaded ? 'disabled' : ''}>
                                    <i class="fas fa-volume-up"></i>
                                </button>
                                <div class="dropdown">
                                    <button class="btn btn-outline-secondary" data-bs-toggle="dropdown">
                                        <i class="fas fa-ellipsis-v"></i>
                                    </button>
                                    <ul class="dropdown-menu dropdown-menu-end">
                                        <li><a class="dropdown-item" href="#" onclick="editSink('${escapeHtml(name)}'); return false;">
                                            <i class="fas fa-edit me-2"></i>Edit</a></li>
                                        <li><a class="dropdown-item" href="#" onclick="reloadSink('${escapeHtml(name)}'); return false;">
                                            <i class="fas fa-sync me-2"></i>Reload</a></li>
                                        <li><hr class="dropdown-divider"></li>
                                        <li><a class="dropdown-item text-danger" href="#" onclick="deleteSink('${escapeHtml(name)}'); return false;">
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

    // Populate device list
    const deviceList = document.getElementById('combineDeviceList');
    if (devices.length === 0) {
        deviceList.innerHTML = '<div class="text-center py-2 text-muted">No devices available</div>';
    } else {
        const selectedSlaves = editData?.slaves || [];
        deviceList.innerHTML = devices.map(d => `
            <div class="form-check device-checkbox-item">
                <input class="form-check-input" type="checkbox" value="${escapeHtml(d.id)}" id="combine-${escapeHtml(d.id)}"
                    ${selectedSlaves.includes(d.id) ? 'checked' : ''}>
                <label class="form-check-label" for="combine-${escapeHtml(d.id)}">
                    ${escapeHtml(d.alias || d.name)}
                    ${d.isDefault ? '<span class="badge bg-primary ms-1">default</span>' : ''}
                </label>
            </div>
        `).join('');
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

    // Populate master device dropdown
    const masterSelect = document.getElementById('remapMasterDevice');
    masterSelect.innerHTML = '<option value="">Select a device...</option>' +
        devices.map(d => `<option value="${escapeHtml(d.id)}">${escapeHtml(d.alias || d.name)} (${d.maxChannels}ch)</option>`).join('');

    // Set master device if editing
    if (editData?.masterSink) {
        masterSelect.value = editData.masterSink;
    }

    updateChannelPicker();

    // Set channel mappings if editing
    if (editData?.channelMappings && editData.channelMappings.length >= 2) {
        const leftMapping = editData.channelMappings.find(m => m.outputChannel === 'front-left');
        const rightMapping = editData.channelMappings.find(m => m.outputChannel === 'front-right');
        if (leftMapping) {
            document.getElementById('leftChannel').value = leftMapping.masterChannel;
        }
        if (rightMapping) {
            document.getElementById('rightChannel').value = rightMapping.masterChannel;
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

// Update channel picker based on selected master device
function updateChannelPicker() {
    const masterSelect = document.getElementById('remapMasterDevice');
    const leftChannel = document.getElementById('leftChannel');
    const rightChannel = document.getElementById('rightChannel');

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

    leftChannel.innerHTML = optionsHtml;
    rightChannel.innerHTML = optionsHtml;

    // Set defaults
    leftChannel.value = 'front-left';
    rightChannel.value = 'front-right';
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
    const leftChannel = document.getElementById('leftChannel').value;
    const rightChannel = document.getElementById('rightChannel').value;
    const isEditing = !!editingRemapSink;

    if (!name) {
        showAlert('Please enter a sink name', 'warning');
        return;
    }

    if (!masterSink) {
        showAlert('Please select a master device', 'warning');
        return;
    }

    const channelMappings = [
        { outputChannel: 'front-left', masterChannel: leftChannel },
        { outputChannel: 'front-right', masterChannel: rightChannel }
    ];

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
        const response = await fetch('./api/sinks/remap', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                name,
                description: description || null,
                masterSink,
                channels: 2,
                channelMappings,
                remix: false
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
    if (!confirm(`Delete custom sink "${name}"?`)) return;

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

// Open the sound cards configuration modal
async function openSoundCardsModal() {
    if (!soundCardsModal) {
        soundCardsModal = new bootstrap.Modal(document.getElementById('soundCardsModal'));
    }

    soundCardsModal.show();
    await loadSoundCards();
}

// Load sound cards from API
async function loadSoundCards() {
    const container = document.getElementById('soundCardsContainer');
    container.innerHTML = `
        <div class="text-center py-4">
            <div class="spinner-border text-primary" role="status">
                <span class="visually-hidden">Loading...</span>
            </div>
            <p class="mt-2 text-muted">Loading sound cards...</p>
        </div>
    `;

    try {
        const response = await fetch('./api/cards');
        if (!response.ok) {
            throw new Error('Failed to load sound cards');
        }

        const data = await response.json();
        soundCards = data.cards || [];

        renderSoundCards();
    } catch (error) {
        console.error('Error loading sound cards:', error);
        container.innerHTML = `
            <div class="alert alert-danger">
                <i class="fas fa-exclamation-triangle me-2"></i>
                Failed to load sound cards: ${escapeHtml(error.message)}
            </div>
        `;
    }
}

// Render sound cards list
function renderSoundCards() {
    const container = document.getElementById('soundCardsContainer');

    if (soundCards.length === 0) {
        container.innerHTML = `
            <div class="text-center py-4 text-muted">
                <i class="fas fa-sd-card fa-3x mb-3 opacity-50"></i>
                <p class="mb-0">No sound cards detected</p>
            </div>
        `;
        return;
    }

    const cardsHtml = soundCards.map(card => {
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

        return `
            <div class="card mb-3" id="settings-card-${card.index}">
                <div class="card-body">
                    <div class="d-flex justify-content-between align-items-start mb-2">
                        <div>
                            <h6 class="mb-1">
                                <i class="fas fa-sd-card text-primary me-2"></i>
                                ${escapeHtml(card.description || card.name)}
                            </h6>
                            <small class="text-muted">${escapeHtml(card.driver)}</small>
                        </div>
                        <span class="badge bg-secondary" id="settings-card-status-${card.index}">
                            ${escapeHtml(activeDesc)}
                        </span>
                    </div>

                    ${hasMultipleProfiles ? `
                        <div class="mb-2">
                            <label class="form-label small text-muted mb-1">Audio Profile</label>
                            <select class="form-select"
                                    id="settings-profile-select-${card.index}"
                                    onchange="setSoundCardProfile('${escapeHtml(card.name)}', this.value, ${card.index})">
                                ${profileOptions}
                            </select>
                        </div>
                        <div id="settings-card-message-${card.index}" class="small"></div>
                    ` : `
                        <div class="text-muted small">
                            <i class="fas fa-check-circle text-success me-1"></i>
                            Single profile available: ${escapeHtml(activeDesc)}
                        </div>
                    `}
                </div>
            </div>
        `;
    }).join('');

    container.innerHTML = cardsHtml;
}

// Set a sound card's profile
async function setSoundCardProfile(cardName, profileName, cardIndex) {
    const select = document.getElementById(`settings-profile-select-${cardIndex}`);
    const statusBadge = document.getElementById(`settings-card-status-${cardIndex}`);
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
    if (!confirm('Clear all logs? This cannot be undone.')) return;

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
    const content = logsData.map(e =>
        `${e.timestamp}|${e.level}|${e.category}|${e.message}${e.exception ? '|' + e.exception : ''}`
    ).join('\n');

    const blob = new Blob([content], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `multiroom-audio-logs-${new Date().toISOString().slice(0,10)}.txt`;
    a.click();
    URL.revokeObjectURL(url);
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
    if (!confirm('Reset first-run state? The setup wizard will appear on the next page load.')) {
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
