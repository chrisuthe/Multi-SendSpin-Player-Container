// Multi-Room Audio Controller - JavaScript App

// State
let players = {};
let devices = [];
let connection = null;

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

// Initialize app
document.addEventListener('DOMContentLoaded', async () => {
    // Load initial data
    await Promise.all([
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
                option.textContent = `${device.name}${device.isDefault ? ' (default)' : ''}`;
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
        if (player.device) {
            document.getElementById('audioDevice').value = player.device;
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

            // If restart is needed, trigger it
            if (result.needsRestart) {
                const restartResponse = await fetch(`./api/players/${encodeURIComponent(finalName)}/restart`, {
                    method: 'POST'
                });
                if (!restartResponse.ok) {
                    console.warn('Restart request failed, player may need manual restart');
                }
            }

            // Close modal and refresh
            bootstrap.Modal.getInstance(document.getElementById('playerModal')).hide();
            document.getElementById('playerForm').reset();
            document.getElementById('initialVolumeValue').textContent = '75%';
            await refreshStatus();

            if (result.needsRestart) {
                showAlert(`Player "${finalName}" updated and restarted`, 'success');
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

async function showPlayerStats(name) {
    const player = players[name];
    if (!player) return;

    const modal = document.getElementById('playerStatsModal');
    const body = document.getElementById('playerStatsBody');

    body.innerHTML = `
        <div class="row">
            <div class="col-md-6">
                <h6 class="text-muted text-uppercase small">Configuration</h6>
                <table class="table table-sm">
                    <tr><td><strong>Name</strong></td><td>${escapeHtml(player.name)}</td></tr>
                    <tr><td><strong>Device</strong></td><td>${escapeHtml(player.device || 'default')}</td></tr>
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
                <h6 class="text-muted text-uppercase small">Output Format</h6>
                ${player.outputFormat ? `
                <table class="table table-sm">
                    <tr><td><strong>Sample Rate</strong></td><td>${formatSampleRate(player.outputFormat.sampleRate)}</td></tr>
                    <tr><td><strong>Bit Depth</strong></td><td>${player.outputFormat.bitDepth}-bit</td></tr>
                    <tr><td><strong>Channels</strong></td><td>${player.outputFormat.channels}ch (${player.outputFormat.channels === 2 ? 'Stereo' : player.outputFormat.channels === 1 ? 'Mono' : 'Multi-channel'})</td></tr>
                </table>
                ` : '<p class="text-muted small">Using default format</p>'}
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
        <div class="delay-control d-flex align-items-center gap-2">
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
            <small class="text-muted ms-2">Range: -5000 to +5000ms</small>
            <span id="delaySavedIndicator" class="text-success ms-2 small" style="opacity: 0; transition: opacity 0.3s;"><i class="fas fa-check"></i> Saved</span>
        </div>
        ${player.metrics ? `
        <h6 class="text-muted text-uppercase small mt-3">Metrics</h6>
        <div class="d-flex flex-wrap">
            <span class="metric-badge bg-light border"><i class="fas fa-database"></i> Buffer: ${player.metrics.bufferLevel}/${player.metrics.bufferCapacity}ms</span>
            <span class="metric-badge bg-light border"><i class="fas fa-music"></i> Samples: ${player.metrics.samplesPlayed.toLocaleString()}</span>
            <span class="metric-badge ${player.metrics.underruns > 0 ? 'bg-warning' : 'bg-light border'}"><i class="fas fa-exclamation-triangle"></i> Underruns: ${player.metrics.underruns}</span>
            <span class="metric-badge ${player.metrics.overruns > 0 ? 'bg-warning' : 'bg-light border'}"><i class="fas fa-level-up-alt"></i> Overruns: ${player.metrics.overruns}</span>
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
                            <small class="text-muted ms-2">${escapeHtml(player.device || 'default')}</small>
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

                        ${player.isClockSynced ? `
                            <small class="text-success"><i class="fas fa-clock me-1"></i>Clock synced</small>
                        ` : ''}
                        ${player.errorMessage ? `
                            <small class="text-danger d-block"><i class="fas fa-exclamation-circle me-1"></i>${escapeHtml(player.errorMessage)}</small>
                        ` : ''}
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
function showAlert(message, type = 'info') {
    const container = document.getElementById('alert-container');
    const alert = document.createElement('div');
    alert.className = `alert alert-${type} alert-dismissible fade show`;
    alert.innerHTML = `
        ${escapeHtml(message)}
        <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
    `;
    container.appendChild(alert);

    // Auto-dismiss after 5 seconds
    setTimeout(() => {
        alert.classList.remove('show');
        setTimeout(() => alert.remove(), 150);
    }, 5000);
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
                <span class="stats-label">Correction Mode</span>
                <span class="stats-value ${getCorrectionModeClass(stats.sync.correctionMode)}">${escapeHtml(stats.sync.correctionMode)}</span>
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
                <span class="stats-label">Playback Rate</span>
                <span class="stats-value ${getPlaybackRateClass(stats.sync.playbackRate)}">${stats.sync.playbackRate.toFixed(4)}x</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Dropped (Sync)</span>
                <span class="stats-value ${stats.throughput.samplesDroppedSync > 0 ? 'warning' : ''}">${formatSampleCount(stats.throughput.samplesDroppedSync)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Inserted (Sync)</span>
                <span class="stats-value ${stats.throughput.samplesInsertedSync > 0 ? 'warning' : ''}">${formatSampleCount(stats.throughput.samplesInsertedSync)}</span>
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

        <!-- Format Conversion Section -->
        <div class="stats-section">
            <div class="stats-section-header">Format Conversion</div>
            <div class="stats-row">
                <span class="stats-label">Rate</span>
                <span class="stats-value info">${formatSampleRate(stats.resampler.inputRate)} → ${formatSampleRate(stats.resampler.outputRate)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Handler</span>
                <span class="stats-value">${escapeHtml(stats.resampler.quality)}</span>
            </div>
            <div class="stats-row">
                <span class="stats-label">Effective Ratio</span>
                <span class="stats-value">${stats.resampler.effectiveRatio.toFixed(6)}</span>
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
        case 'None': return '';
        case 'Resampling': return 'info';
        case 'Dropping': return 'warning';
        case 'Inserting': return 'warning';
        default: return '';
    }
}

function getPlaybackRateClass(rate) {
    if (rate === 1.0) return '';
    if (rate > 1.0) return 'info';  // speeding up
    return 'info';  // slowing down
}
