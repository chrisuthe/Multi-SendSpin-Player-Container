// =============================================================================
// Onboarding Wizard
// =============================================================================
// This module handles the first-time setup wizard for Multi-Room Audio.
// It guides users through device discovery, identification, naming, and player creation.

// Parse bus_path into user-friendly USB port hint
// Handles Linux sysfs format: /devices/pci.../usb3/3-2/3-2.4/3-2.4:1.0/sound/card1
// e.g., "3-2.4" → "Port 4"
// e.g., "3-2.1.1" → "Port 1 → Hub Port 1"
function parseUsbPortHint(busPath) {
    if (!busPath) return null;

    // Check if this is a USB device path
    if (!busPath.includes('/usb')) return null;

    // Extract the port segment before /sound/ - format: "{bus}-{port.subport...}:interface"
    // e.g., "/usb3/3-2/3-2.4/3-2.4:1.0/sound/" → "3-2.4"
    const portMatch = busPath.match(/\/(\d+-[\d.]+):[\d.]+\/sound\//);
    if (!portMatch) return null;

    const fullPort = portMatch[1];  // e.g., "3-2.4" or "3-2.1.1"

    // Split by hyphen to get bus and port path
    const hyphenIdx = fullPort.indexOf('-');
    if (hyphenIdx === -1) return null;

    const portPath = fullPort.substring(hyphenIdx + 1);  // e.g., "2.4" or "2.1.1"

    // Split by dots to get port hierarchy
    const parts = portPath.split('.');
    if (parts.length < 2) return null;  // Need at least root.port

    // Skip the root hub port (first part), show the rest
    // "2.4" → ["2", "4"] → "Port 4"
    // "2.1.1" → ["2", "1", "1"] → "Port 1 → Hub Port 1"
    const portParts = parts.slice(1);  // Skip root hub

    if (portParts.length === 0) return null;

    if (portParts.length === 1) {
        return `Port ${portParts[0]}`;
    } else {
        // Multi-level: "Port 1 → Hub Port 1"
        let hint = `Port ${portParts[0]}`;
        for (let i = 1; i < portParts.length; i++) {
            hint += ` → Hub Port ${portParts[i]}`;
        }
        return hint;
    }
}

const Wizard = {
    // Wizard state
    currentStep: 0,
    devices: [],
    deviceState: {},  // deviceId -> { alias, hidden: bool }
    cards: [],        // Sound cards with profiles
    customSinks: [],
    playersToCreate: [],
    modal: null,

    // Step definitions
    // Note: 'cards' step is conditionally shown only if there are cards with multiple profiles
    STEPS: [
        { id: 'welcome', title: 'Welcome' },
        { id: 'cards', title: 'Cards' },
        { id: 'identify', title: 'Identify' },
        { id: 'sinks', title: 'Sinks' },
        { id: 'players', title: 'Players' },
        { id: 'complete', title: 'Done' }
    ],

    // Initialize wizard
    async init() {
        this.modal = new bootstrap.Modal(document.getElementById('onboardingWizard'), {
            backdrop: 'static',
            keyboard: false
        });

        // Set up event listeners
        document.getElementById('wizardPrev').addEventListener('click', () => this.prevStep());
        document.getElementById('wizardNext').addEventListener('click', () => this.nextStep());
        document.getElementById('wizardSkip').addEventListener('click', () => this.skip());
    },

    // Check if onboarding should be shown
    async shouldShow() {
        try {
            const response = await fetch('./api/onboarding/status');
            const data = await response.json();
            return data.shouldShow;
        } catch (error) {
            console.error('Failed to check onboarding status:', error);
            return false;
        }
    },

    // Show the wizard
    async show() {
        this.currentStep = 0;
        this.devices = [];
        this.deviceState = {};
        this.cards = [];
        this.customSinks = [];
        this.playersToCreate = [];

        // Load cards first so progress indicator shows Cards step if needed
        await this.loadCards();

        this.renderProgress();
        this.renderStep();
        this.modal.show();
    },

    // Hide the wizard
    hide() {
        this.modal.hide();
    },

    // Skip onboarding
    async skip() {
        try {
            await fetch('./api/onboarding/skip', { method: 'POST' });
            this.hide();
            showAlert('Setup skipped. You can run the wizard again from the settings menu.', 'info');
        } catch (error) {
            console.error('Failed to skip onboarding:', error);
        }
    },

    // Navigate to previous step
    prevStep() {
        if (this.currentStep > 0) {
            this.currentStep--;
            // Skip cards step if no multi-profile cards (when going back)
            if (this.STEPS[this.currentStep].id === 'cards' && !this.hasMultiProfileCards()) {
                this.currentStep--;
            }
            this.renderProgress();
            this.renderStep();
        }
    },

    // Navigate to next step
    async nextStep() {
        // Validate and save current step
        const valid = await this.validateStep();
        if (!valid) return;

        if (this.currentStep < this.STEPS.length - 1) {
            this.currentStep++;
            this.renderProgress();
            this.renderStep();
        } else {
            // Complete wizard
            await this.complete();
        }
    },

    // Validate current step before proceeding
    async validateStep() {
        const step = this.STEPS[this.currentStep];

        switch (step.id) {
            case 'cards':
                // Clear devices cache when leaving cards step so they reload with new profiles
                this.devices = [];
                return true;
            case 'identify':
                // Save aliases when leaving identify step
                await this.saveAliases();
                return true;
            case 'players':
                // Collect player creation data
                this.collectPlayersToCreate();
                return true;
            default:
                return true;
        }
    },

    // Render progress indicator
    renderProgress() {
        const container = document.getElementById('wizardProgress');
        const showCards = this.hasMultiProfileCards();

        // Filter steps - hide cards step if no multi-profile cards
        const visibleSteps = this.STEPS.filter(step =>
            step.id !== 'cards' || showCards
        );

        container.innerHTML = visibleSteps.map((step, displayIndex) => {
            // Find actual index in STEPS array to compare with currentStep
            const actualIndex = this.STEPS.findIndex(s => s.id === step.id);
            let stateClass = '';
            if (actualIndex < this.currentStep) stateClass = 'completed';
            else if (actualIndex === this.currentStep) stateClass = 'active';

            return `
                <div class="wizard-step ${stateClass}">
                    <div class="wizard-step-indicator">
                        ${actualIndex < this.currentStep ? '<i class="fas fa-check"></i>' : displayIndex + 1}
                    </div>
                    <div class="wizard-step-label">${step.title}</div>
                </div>
            `;
        }).join('');
    },

    // Render current step content
    async renderStep() {
        const step = this.STEPS[this.currentStep];
        const content = document.getElementById('wizardContent');
        const prevBtn = document.getElementById('wizardPrev');
        const nextBtn = document.getElementById('wizardNext');
        const skipBtn = document.getElementById('wizardSkip');

        // Update button states
        prevBtn.style.display = this.currentStep > 0 ? 'inline-block' : 'none';
        skipBtn.style.display = this.currentStep < this.STEPS.length - 1 ? 'inline-block' : 'none';

        // Update next button text
        if (this.currentStep === this.STEPS.length - 1) {
            nextBtn.textContent = 'Finish';
        } else if (this.currentStep === this.STEPS.length - 2) {
            nextBtn.textContent = 'Create Players';
        } else {
            nextBtn.textContent = 'Continue';
        }

        // Render step content
        switch (step.id) {
            case 'welcome':
                content.innerHTML = this.renderWelcome();
                break;
            case 'cards':
                // Load cards if not already loaded
                if (this.cards.length === 0) {
                    await this.loadCards();
                }
                // Skip cards step if no multi-profile cards
                if (!this.hasMultiProfileCards()) {
                    this.currentStep++;
                    this.renderProgress();
                    await this.renderStep();
                    return;
                }
                content.innerHTML = this.renderCards();
                break;
            case 'identify':
                content.innerHTML = this.renderIdentify();
                // Load devices if not already loaded
                if (this.devices.length === 0) {
                    await this.loadDevicesForIdentify();
                }
                break;
            case 'sinks':
                content.innerHTML = this.renderSinks();
                break;
            case 'players':
                content.innerHTML = this.renderPlayers();
                break;
            case 'complete':
                content.innerHTML = this.renderComplete();
                break;
        }
    },

    // Step 1: Welcome
    renderWelcome() {
        return `
            <div class="text-center py-4">
                <i class="fas fa-music fa-4x text-primary mb-4"></i>
                <h2>Welcome to Multi-Room Audio</h2>
                <p class="text-muted lead mb-4">
                    Let's set up your audio outputs. This wizard will help you:
                </p>
                <ul class="list-unstyled text-start mx-auto" style="max-width: 400px;">
                    <li class="mb-2"><i class="fas fa-check text-success me-2"></i>Identify speakers by playing test tones</li>
                    <li class="mb-2"><i class="fas fa-check text-success me-2"></i>Give friendly names to each output</li>
                    <li class="mb-2"><i class="fas fa-check text-success me-2"></i>Create audio players</li>
                </ul>
            </div>
        `;
    },

    // Check if there are cards with multiple available profiles
    hasMultiProfileCards() {
        return this.cards.some(card => {
            const availableProfiles = card.profiles.filter(p => p.isAvailable);
            return availableProfiles.length > 1;
        });
    },

    // Load sound cards from API
    async loadCards() {
        try {
            const response = await fetch('./api/cards');
            if (!response.ok) {
                console.warn('Failed to load cards:', response.status);
                this.cards = [];
                return;
            }
            const data = await response.json();
            this.cards = data.cards || [];
        } catch (error) {
            console.error('Failed to load cards:', error);
            this.cards = [];
        }
    },

    // Step 2: Sound Card Profiles (only shown if multi-profile cards exist)
    renderCards() {
        // Filter to cards with multiple available profiles
        const multiProfileCards = this.cards.filter(card => {
            const availableProfiles = card.profiles.filter(p => p.isAvailable);
            return availableProfiles.length > 1;
        });

        const cardHtml = multiProfileCards.map(card => {
            const availableProfiles = card.profiles.filter(p => p.isAvailable);

            const profileOptions = availableProfiles.map(profile => {
                const isActive = profile.name === card.activeProfile;
                // Create a friendly description
                let label = profile.description || profile.name;
                if (profile.sinks > 0 || profile.sources > 0) {
                    const parts = [];
                    if (profile.sinks > 0) parts.push(`${profile.sinks} output${profile.sinks > 1 ? 's' : ''}`);
                    if (profile.sources > 0) parts.push(`${profile.sources} input${profile.sources > 1 ? 's' : ''}`);
                    label += ` (${parts.join(', ')})`;
                }
                return `<option value="${escapeHtml(profile.name)}" ${isActive ? 'selected' : ''}>${escapeHtml(label)}</option>`;
            }).join('');

            // Find active profile description
            const activeProfile = availableProfiles.find(p => p.name === card.activeProfile);
            const activeDesc = activeProfile?.description || card.activeProfile;

            return `
                <div class="card mb-3" id="card-${card.index}">
                    <div class="card-body">
                        <div class="d-flex justify-content-between align-items-start mb-2">
                            <div>
                                <h6 class="mb-1">
                                    <i class="fas fa-sd-card text-primary me-2"></i>
                                    ${escapeHtml(card.description || card.name)}
                                </h6>
                                <small class="text-muted">${escapeHtml(card.driver)}</small>
                            </div>
                            <span class="badge bg-secondary" id="card-status-${card.index}">
                                ${escapeHtml(activeDesc)}
                            </span>
                        </div>

                        <div class="mb-2">
                            <label class="form-label small text-muted mb-1">Audio Profile</label>
                            <select class="form-select"
                                    id="profile-select-${card.index}"
                                    onchange="Wizard.setCardProfile('${escapeHtml(card.name)}', this.value, ${card.index})">
                                ${profileOptions}
                            </select>
                        </div>

                        <div id="card-message-${card.index}" class="small"></div>
                    </div>
                </div>
            `;
        }).join('');

        return `
            <div>
                <h4><i class="fas fa-sd-card me-2"></i>Sound Card Configuration</h4>
                <p class="text-muted">
                    Some of your sound cards support multiple output modes. Choose the profile that matches
                    how you want to use each card (e.g., stereo, surround, or multi-channel output).
                </p>

                <div class="alert alert-info mb-3">
                    <i class="fas fa-info-circle me-2"></i>
                    <strong>Tip:</strong> Built-in sound cards often support 5.1 or 7.1 surround profiles that expose
                    multiple outputs. Multi-channel USB interfaces can also use surround profiles to access individual
                    channel pairs as separate stereo outputs.
                </div>

                <div id="wizardCardsList">
                    ${cardHtml}
                </div>
            </div>
        `;
    },

    // Set a card's profile
    async setCardProfile(cardName, profileName, cardIndex) {
        const select = document.getElementById(`profile-select-${cardIndex}`);
        const statusBadge = document.getElementById(`card-status-${cardIndex}`);
        const messageDiv = document.getElementById(`card-message-${cardIndex}`);

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
            const card = this.cards.find(c => c.name === cardName);
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

        } catch (error) {
            console.error('Failed to set card profile:', error);
            if (messageDiv) {
                messageDiv.innerHTML = `<i class="fas fa-exclamation-triangle me-1"></i>${escapeHtml(error.message)}`;
                messageDiv.className = 'small text-danger';
            }
            // Revert select to previous value
            const card = this.cards.find(c => c.name === cardName);
            if (select && card) {
                select.value = card.activeProfile;
            }
        } finally {
            if (select) select.disabled = false;
        }
    },

    // Step 3: Identify Devices (combines discovery and naming)
    renderIdentify() {
        // Show loading state if devices not loaded yet
        if (this.devices.length === 0) {
            return `
                <div>
                    <h4><i class="fas fa-headphones me-2"></i>Identify Your Speakers</h4>
                    <p class="text-muted">
                        Click "Test" to hear a sound from each device, then give it a name.
                        Use "Hide" to exclude outputs you won't use (like HDMI).
                    </p>

                    <div id="wizardIdentifyList" class="list-group mb-3">
                        <div class="text-center py-4">
                            <div class="spinner-border text-primary" role="status">
                                <span class="visually-hidden">Loading...</span>
                            </div>
                            <p class="text-muted mt-2">Discovering devices...</p>
                        </div>
                    </div>
                </div>
            `;
        }

        const deviceHtml = this.devices.map(device => {
            const isHidden = this.deviceState[device.id]?.hidden || device.hidden || false;
            const alias = this.deviceState[device.id]?.alias || '';
            const portHint = parseUsbPortHint(device.identifiers?.busPath);

            return `
                <div class="list-group-item position-relative ${isHidden ? 'device-hidden' : ''}" id="device-row-${escapeHtml(device.id)}">
                    ${isHidden ? '<div class="device-hidden-overlay"></div>' : ''}
                    <div class="d-flex justify-content-between align-items-start">
                        <div class="flex-grow-1 me-3">
                            <h6 class="mb-1">
                                ${escapeHtml(device.alias || device.name)}
                                ${device.isDefault ? '<span class="badge bg-primary ms-2">Default</span>' : ''}
                                ${isHidden ? '<span class="badge bg-secondary ms-2">Hidden</span>' : ''}
                            </h6>
                            <small class="text-muted d-block">${device.maxChannels}ch, ${formatSampleRate(device.defaultSampleRate)}${portHint ? ` · <i class="fab fa-usb"></i> ${portHint}` : ''}</small>
                            <div class="input-group input-group-sm mt-2" style="max-width: 300px;">
                                <input type="text" class="form-control"
                                       placeholder="e.g., Kitchen Speaker"
                                       id="alias-${escapeHtml(device.id)}"
                                       value="${escapeHtml(alias)}"
                                       ${isHidden ? 'disabled' : ''}
                                       onchange="Wizard.setAlias('${escapeHtml(device.id)}', this.value)">
                            </div>
                        </div>
                        <div class="btn-group-vertical">
                            <button class="btn btn-outline-primary btn-sm"
                                    onclick="Wizard.playTestTone('${escapeHtml(device.id)}')"
                                    id="tone-btn-${escapeHtml(device.id)}"
                                    ${isHidden ? 'disabled' : ''}>
                                <i class="fas fa-volume-up me-1"></i>Test
                            </button>
                            <button class="btn ${isHidden ? 'btn-secondary' : 'btn-outline-secondary'} btn-sm"
                                    onclick="Wizard.toggleHidden('${escapeHtml(device.id)}')"
                                    id="hide-btn-${escapeHtml(device.id)}">
                                <i class="fas fa-${isHidden ? 'eye' : 'eye-slash'} me-1"></i>${isHidden ? 'Show' : 'Hide'}
                            </button>
                        </div>
                    </div>
                </div>
            `;
        }).join('');

        const hiddenCount = this.devices.filter(d => this.deviceState[d.id]?.hidden || d.hidden).length;

        return `
            <div>
                <h4><i class="fas fa-headphones me-2"></i>Identify Your Speakers</h4>
                <p class="text-muted">
                    Click "Test" to hear a sound from each device, then give it a name.
                    Use "Hide" to exclude outputs you won't use (like HDMI).
                </p>

                <div id="wizardIdentifyList" class="list-group mb-3">
                    ${deviceHtml}
                </div>

                <button class="btn btn-outline-secondary btn-sm mb-3" onclick="Wizard.refreshDevicesForIdentify()">
                    <i class="fas fa-sync-alt me-1"></i>Refresh Devices
                </button>

                ${hiddenCount > 0 ? `
                    <div class="alert alert-info mb-0">
                        <i class="fas fa-info-circle me-2"></i>
                        ${hiddenCount} output(s) hidden. Hidden outputs won't appear as players by default,
                        but you can still access them in player settings if needed.
                    </div>
                ` : ''}
            </div>
        `;
    },

    // Step 3: Custom Sinks (Optional)
    renderSinks() {
        // Build device checkboxes for combine sink (exclude hidden devices)
        const visibleDevices = this.devices.filter(d =>
            !d.id.includes('.monitor') &&
            !(this.deviceState[d.id]?.hidden || d.hidden)
        );
        const deviceCheckboxes = visibleDevices
            .map(d => `
                <div class="form-check">
                    <input class="form-check-input" type="checkbox"
                           value="${escapeHtml(d.id)}"
                           id="wizard-combine-${escapeHtml(d.id)}">
                    <label class="form-check-label" for="wizard-combine-${escapeHtml(d.id)}">
                        ${escapeHtml(this.deviceState[d.id]?.alias || d.alias || d.name)}
                    </label>
                </div>
            `).join('');

        // Build device options for remap sink (exclude hidden)
        const multiChannelDevices = visibleDevices.filter(d => d.maxChannels >= 4);
        const deviceOptions = multiChannelDevices.map(d => `
            <option value="${escapeHtml(d.id)}" data-channels="${d.maxChannels}">
                ${escapeHtml(this.deviceState[d.id]?.alias || d.alias || d.name)} (${d.maxChannels}ch)
            </option>
        `).join('');

        // Build created sinks list
        const sinksListHtml = this.customSinks.length > 0
            ? this.customSinks.map(sink => `
                <div class="list-group-item d-flex justify-content-between align-items-center">
                    <div>
                        <strong>${escapeHtml(sink.name)}</strong>
                        <span class="badge bg-${sink.type === 'combine' ? 'info' : 'secondary'} ms-2">
                            ${sink.type}
                        </span>
                        <small class="text-muted d-block">${escapeHtml(sink.description || '')}</small>
                    </div>
                    <button class="btn btn-outline-danger btn-sm"
                            onclick="Wizard.removeCustomSink('${escapeHtml(sink.id)}')">
                        <i class="fas fa-trash"></i>
                    </button>
                </div>
            `).join('')
            : '<div class="list-group-item text-center text-muted py-3">No custom sinks created yet</div>';

        return `
            <div>
                <h4><i class="fas fa-layer-group me-2"></i>Custom Audio Sinks</h4>
                <p class="text-muted">
                    Create virtual audio outputs for advanced setups. <strong>This step is optional</strong> —
                    most users can skip directly to creating players.
                </p>

                <!-- Combine Sink Section -->
                <div class="card mb-3">
                    <div class="card-header d-flex justify-content-between align-items-center"
                         style="cursor: pointer;"
                         data-bs-toggle="collapse"
                         data-bs-target="#combineSinkSection">
                        <span><i class="fas fa-layer-group text-info me-2"></i><strong>Combine Sink</strong></span>
                        <i class="fas fa-chevron-down"></i>
                    </div>
                    <div class="collapse" id="combineSinkSection">
                        <div class="card-body">
                            <div class="alert alert-light border mb-3">
                                <i class="fas fa-lightbulb text-warning me-2"></i>
                                <strong>What is this?</strong> A combine sink plays audio to multiple speakers
                                simultaneously. Perfect for whole-home audio or synchronized outputs like
                                "Kitchen + Dining Room".
                            </div>

                            <div class="row">
                                <div class="col-md-6">
                                    <div class="mb-3">
                                        <label class="form-label">Sink Name <span class="text-danger">*</span></label>
                                        <input type="text" class="form-control" id="wizardCombineName"
                                               placeholder="kitchen_dining">
                                        <small class="text-muted">Letters, numbers, underscores, hyphens only</small>
                                    </div>
                                    <div class="mb-3">
                                        <label class="form-label">Description</label>
                                        <input type="text" class="form-control" id="wizardCombineDesc"
                                               placeholder="Kitchen + Dining Room">
                                    </div>
                                </div>
                                <div class="col-md-6">
                                    <label class="form-label">Select Devices to Combine <span class="text-danger">*</span></label>
                                    <div class="border rounded p-2" style="max-height: 150px; overflow-y: auto;">
                                        ${deviceCheckboxes || '<div class="text-muted">No devices available</div>'}
                                    </div>
                                    <small class="text-muted">Select at least 2 devices</small>
                                </div>
                            </div>

                            <div id="wizardCombineError" class="alert alert-danger d-none mb-3"></div>
                            <div id="wizardCombineSuccess" class="alert alert-success d-none mb-3"></div>

                            <button class="btn btn-info" onclick="Wizard.createCombineSink()">
                                <i class="fas fa-plus me-1"></i>Create Combine Sink
                            </button>
                        </div>
                    </div>
                </div>

                <!-- Remap Sink Section -->
                <div class="card mb-3">
                    <div class="card-header d-flex justify-content-between align-items-center"
                         style="cursor: pointer;"
                         data-bs-toggle="collapse"
                         data-bs-target="#remapSinkSection">
                        <span><i class="fas fa-random text-secondary me-2"></i><strong>Remap Sink</strong></span>
                        <i class="fas fa-chevron-down"></i>
                    </div>
                    <div class="collapse" id="remapSinkSection">
                        <div class="card-body">
                            <div class="alert alert-light border mb-3">
                                <i class="fas fa-lightbulb text-warning me-2"></i>
                                <strong>What is this?</strong> A remap sink extracts specific channels from a
                                multi-channel audio device. Useful if you have a 5.1 or 7.1 surround card and
                                want to use individual channel pairs as separate stereo outputs.
                            </div>

                            ${multiChannelDevices.length === 0 ? `
                                <div class="alert alert-secondary">
                                    <i class="fas fa-info-circle me-2"></i>
                                    No multi-channel devices (4+ channels) detected. Remap sinks require
                                    devices with more than 2 channels.
                                </div>
                            ` : `
                                <div class="row">
                                    <div class="col-md-6">
                                        <div class="mb-3">
                                            <label class="form-label">Sink Name <span class="text-danger">*</span></label>
                                            <input type="text" class="form-control" id="wizardRemapName"
                                                   placeholder="surround_rear">
                                            <small class="text-muted">Letters, numbers, underscores, hyphens only</small>
                                        </div>
                                        <div class="mb-3">
                                            <label class="form-label">Description</label>
                                            <input type="text" class="form-control" id="wizardRemapDesc"
                                                   placeholder="Surround Card - Rear Speakers">
                                        </div>
                                        <div class="mb-3">
                                            <label class="form-label">Master Device <span class="text-danger">*</span></label>
                                            <select class="form-select" id="wizardRemapMaster"
                                                    onchange="Wizard.updateRemapChannels()">
                                                <option value="">Select a device...</option>
                                                ${deviceOptions}
                                            </select>
                                        </div>
                                    </div>
                                    <div class="col-md-6">
                                        <div class="mb-3">
                                            <label class="form-label">Left Channel</label>
                                            <select class="form-select" id="wizardRemapLeft">
                                                <option value="front-left">Front Left</option>
                                                <option value="front-right">Front Right</option>
                                            </select>
                                        </div>
                                        <div class="mb-3">
                                            <label class="form-label">Right Channel</label>
                                            <select class="form-select" id="wizardRemapRight">
                                                <option value="front-left">Front Left</option>
                                                <option value="front-right" selected>Front Right</option>
                                            </select>
                                        </div>
                                    </div>
                                </div>

                                <div id="wizardRemapError" class="alert alert-danger d-none mb-3"></div>
                                <div id="wizardRemapSuccess" class="alert alert-success d-none mb-3"></div>

                                <button class="btn btn-secondary" onclick="Wizard.createRemapSink()">
                                    <i class="fas fa-plus me-1"></i>Create Remap Sink
                                </button>
                            `}
                        </div>
                    </div>
                </div>

                <!-- Created Sinks -->
                <h5 class="mt-4"><i class="fas fa-check-circle text-success me-2"></i>Created Sinks</h5>
                <div id="wizardSinksList" class="list-group">
                    ${sinksListHtml}
                </div>
            </div>
        `;
    },

    // Update channel options based on selected master device
    updateRemapChannels() {
        const masterSelect = document.getElementById('wizardRemapMaster');
        const leftSelect = document.getElementById('wizardRemapLeft');
        const rightSelect = document.getElementById('wizardRemapRight');

        if (!masterSelect || !leftSelect || !rightSelect) return;

        const selectedOption = masterSelect.selectedOptions[0];
        const channelCount = parseInt(selectedOption?.dataset?.channels || '2');

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
                { value: 'front-right', label: 'Front Right' },
                { value: 'rear-left', label: 'Rear Left' },
                { value: 'rear-right', label: 'Rear Right' }
            ];
        }

        const optionsHtml = channelOptions.map(ch =>
            `<option value="${ch.value}">${ch.label}</option>`
        ).join('');

        leftSelect.innerHTML = optionsHtml;
        rightSelect.innerHTML = optionsHtml;

        // Set sensible defaults
        leftSelect.value = 'front-left';
        rightSelect.value = 'front-right';
    },

    // Create a combine sink
    async createCombineSink() {
        const nameInput = document.getElementById('wizardCombineName');
        const descInput = document.getElementById('wizardCombineDesc');
        const errorDiv = document.getElementById('wizardCombineError');
        const successDiv = document.getElementById('wizardCombineSuccess');

        // Reset messages
        errorDiv.classList.add('d-none');
        successDiv.classList.add('d-none');

        const name = nameInput.value.trim();
        const description = descInput.value.trim();

        // Get selected devices
        const checkboxes = document.querySelectorAll('[id^="wizard-combine-"]:checked');
        const slaves = Array.from(checkboxes).map(cb => cb.value);

        // Validation
        if (!name) {
            errorDiv.textContent = 'Please enter a sink name.';
            errorDiv.classList.remove('d-none');
            nameInput.focus();
            return;
        }

        if (!/^[a-zA-Z0-9_-]+$/.test(name)) {
            errorDiv.textContent = 'Sink name can only contain letters, numbers, underscores, and hyphens.';
            errorDiv.classList.remove('d-none');
            nameInput.focus();
            return;
        }

        if (slaves.length < 2) {
            errorDiv.textContent = 'Please select at least 2 devices to combine.';
            errorDiv.classList.remove('d-none');
            return;
        }

        try {
            const response = await fetch('./api/sinks/combine', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, description: description || null, slaves })
            });

            const result = await response.json();

            if (!response.ok) {
                throw new Error(result.message || result.detail || 'Failed to create combine sink');
            }

            // Add to custom sinks list
            this.customSinks.push({
                id: result.sinkName || name,
                name: result.sinkName || name,
                type: 'combine',
                description: description,
                maxChannels: 2,
                defaultSampleRate: 48000
            });

            // Show success and clear form
            successDiv.innerHTML = `<i class="fas fa-check me-2"></i>Combine sink "<strong>${escapeHtml(name)}</strong>" created successfully!`;
            successDiv.classList.remove('d-none');
            nameInput.value = '';
            descInput.value = '';
            checkboxes.forEach(cb => cb.checked = false);

            // Update the sinks list
            this.updateSinksList();

        } catch (error) {
            errorDiv.textContent = error.message;
            errorDiv.classList.remove('d-none');
        }
    },

    // Create a remap sink
    async createRemapSink() {
        const nameInput = document.getElementById('wizardRemapName');
        const descInput = document.getElementById('wizardRemapDesc');
        const masterSelect = document.getElementById('wizardRemapMaster');
        const leftSelect = document.getElementById('wizardRemapLeft');
        const rightSelect = document.getElementById('wizardRemapRight');
        const errorDiv = document.getElementById('wizardRemapError');
        const successDiv = document.getElementById('wizardRemapSuccess');

        // Reset messages
        errorDiv.classList.add('d-none');
        successDiv.classList.add('d-none');

        const name = nameInput.value.trim();
        const description = descInput.value.trim();
        const masterSink = masterSelect.value;
        const leftChannel = leftSelect.value;
        const rightChannel = rightSelect.value;

        // Validation
        if (!name) {
            errorDiv.textContent = 'Please enter a sink name.';
            errorDiv.classList.remove('d-none');
            nameInput.focus();
            return;
        }

        if (!/^[a-zA-Z0-9_-]+$/.test(name)) {
            errorDiv.textContent = 'Sink name can only contain letters, numbers, underscores, and hyphens.';
            errorDiv.classList.remove('d-none');
            nameInput.focus();
            return;
        }

        if (!masterSink) {
            errorDiv.textContent = 'Please select a master device.';
            errorDiv.classList.remove('d-none');
            return;
        }

        try {
            const response = await fetch('./api/sinks/remap', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    name,
                    description: description || null,
                    masterSink,
                    channels: 2,
                    channelMappings: [
                        { outputChannel: 'front-left', masterChannel: leftChannel },
                        { outputChannel: 'front-right', masterChannel: rightChannel }
                    ]
                })
            });

            const result = await response.json();

            if (!response.ok) {
                throw new Error(result.message || result.detail || 'Failed to create remap sink');
            }

            // Add to custom sinks list
            this.customSinks.push({
                id: result.sinkName || name,
                name: result.sinkName || name,
                type: 'remap',
                description: description,
                maxChannels: 2,
                defaultSampleRate: 48000
            });

            // Show success and clear form
            successDiv.innerHTML = `<i class="fas fa-check me-2"></i>Remap sink "<strong>${escapeHtml(name)}</strong>" created successfully!`;
            successDiv.classList.remove('d-none');
            nameInput.value = '';
            descInput.value = '';
            masterSelect.value = '';

            // Update the sinks list
            this.updateSinksList();

        } catch (error) {
            errorDiv.textContent = error.message;
            errorDiv.classList.remove('d-none');
        }
    },

    // Update the created sinks list display
    updateSinksList() {
        const listDiv = document.getElementById('wizardSinksList');
        if (!listDiv) return;

        if (this.customSinks.length === 0) {
            listDiv.innerHTML = '<div class="list-group-item text-center text-muted py-3">No custom sinks created yet</div>';
            return;
        }

        listDiv.innerHTML = this.customSinks.map(sink => `
            <div class="list-group-item d-flex justify-content-between align-items-center">
                <div>
                    <strong>${escapeHtml(sink.name)}</strong>
                    <span class="badge bg-${sink.type === 'combine' ? 'info' : 'secondary'} ms-2">
                        ${sink.type}
                    </span>
                    <small class="text-muted d-block">${escapeHtml(sink.description || '')}</small>
                </div>
                <button class="btn btn-outline-danger btn-sm"
                        onclick="Wizard.removeCustomSink('${escapeHtml(sink.id)}')"
                        title="Remove this sink">
                    <i class="fas fa-trash"></i>
                </button>
            </div>
        `).join('');
    },

    // Remove a custom sink
    async removeCustomSink(sinkId) {
        if (!confirm(`Remove sink "${sinkId}"? This will unload it from PulseAudio.`)) {
            return;
        }

        try {
            // Try to unload from PulseAudio
            const response = await fetch(`./api/sinks/${encodeURIComponent(sinkId)}`, {
                method: 'DELETE'
            });

            // Remove from local list regardless of API result
            this.customSinks = this.customSinks.filter(s => s.id !== sinkId);
            this.updateSinksList();

            if (!response.ok) {
                console.warn('Sink may not have been unloaded from PulseAudio');
            }
        } catch (error) {
            console.error('Error removing sink:', error);
            // Still remove from local list
            this.customSinks = this.customSinks.filter(s => s.id !== sinkId);
            this.updateSinksList();
        }
    },

    // Step 4: Create Players
    renderPlayers() {
        // Combine devices and custom sinks, excluding hidden devices
        const allDevices = [...this.devices, ...this.customSinks].filter(d =>
            !(this.deviceState[d.id]?.hidden || d.hidden)
        );

        const playerHtml = allDevices.map(device => {
            const alias = this.deviceState[device.id]?.alias || device.alias || this.suggestName(device);
            const isChecked = !device.id.includes('monitor');  // Don't auto-select monitor sinks

            return `
                <div class="card mb-2">
                    <div class="card-body py-2">
                        <div class="form-check form-switch d-flex align-items-center">
                            <input class="form-check-input me-3" type="checkbox"
                                   id="create-player-${escapeHtml(device.id)}"
                                   ${isChecked ? 'checked' : ''}
                                   data-device-id="${escapeHtml(device.id)}"
                                   data-device-name="${escapeHtml(alias)}">
                            <div class="flex-grow-1">
                                <label class="form-check-label fw-bold" for="create-player-${escapeHtml(device.id)}">
                                    ${escapeHtml(alias)}
                                </label>
                                <small class="text-muted d-block text-truncate">${escapeHtml(device.id)}</small>
                            </div>
                            <div class="d-flex align-items-center">
                                <label class="me-2 text-muted small">Vol:</label>
                                <input type="range" class="form-range" style="width: 80px;"
                                       min="0" max="100" value="75"
                                       id="volume-${escapeHtml(device.id)}"
                                       oninput="document.getElementById('volume-label-${escapeHtml(device.id)}').textContent = this.value + '%'">
                                <span class="ms-2 text-muted small" id="volume-label-${escapeHtml(device.id)}" style="width: 35px;">75%</span>
                            </div>
                        </div>
                    </div>
                </div>
            `;
        }).join('');

        const hiddenCount = this.devices.filter(d => this.deviceState[d.id]?.hidden || d.hidden).length;

        return `
            <div>
                <h4><i class="fas fa-play me-2"></i>Create Audio Players</h4>
                <p class="text-muted">
                    Select which devices you want to create players for.
                    Players connect your audio outputs to your music server.
                </p>

                <div id="playerCreationList">
                    ${playerHtml}
                </div>

                ${hiddenCount > 0 ? `
                    <div class="alert alert-secondary mt-3 mb-0">
                        <i class="fas fa-eye-slash me-2"></i>
                        ${hiddenCount} hidden output(s) not shown. You can access them in player settings if needed.
                    </div>
                ` : `
                    <div class="alert alert-info mt-3 mb-0">
                        <i class="fas fa-info-circle me-2"></i>
                        Players will auto-discover your Music Assistant server via mDNS.
                    </div>
                `}
            </div>
        `;
    },

    // Step 5: Complete
    renderComplete() {
        const deviceCount = this.devices.length;
        const aliasCount = Object.values(this.deviceState).filter(d => d.alias).length;
        const hiddenCount = this.devices.filter(d => this.deviceState[d.id]?.hidden || d.hidden).length;
        const sinkCount = this.customSinks.length;
        const playerCount = this.playersToCreate.length;

        return `
            <div class="text-center py-4">
                <i class="fas fa-check-circle fa-4x text-success mb-4"></i>
                <h2>Setup Complete!</h2>
                <p class="text-muted lead">
                    Your multi-room audio system is ready to use.
                </p>

                <div class="card mx-auto" style="max-width: 400px;">
                    <div class="card-body">
                        <h5 class="card-title">Summary</h5>
                        <ul class="list-unstyled text-start mb-0">
                            <li class="mb-2"><i class="fas fa-speaker text-primary me-2"></i>${deviceCount} device(s) discovered</li>
                            <li class="mb-2"><i class="fas fa-tag text-primary me-2"></i>${aliasCount} output(s) named</li>
                            ${hiddenCount > 0 ? `<li class="mb-2"><i class="fas fa-eye-slash text-secondary me-2"></i>${hiddenCount} output(s) hidden</li>` : ''}
                            <li class="mb-2"><i class="fas fa-layer-group text-primary me-2"></i>${sinkCount} custom sink(s)</li>
                            <li><i class="fas fa-play text-primary me-2"></i>${playerCount} player(s) created</li>
                        </ul>
                    </div>
                </div>
            </div>
        `;
    },

    // Load devices from API and re-render the identify step
    async loadDevicesForIdentify() {
        try {
            const response = await fetch('./api/devices');
            const data = await response.json();
            this.devices = data.devices || [];

            // Initialize device state from API data
            this.devices.forEach(device => {
                if (!this.deviceState[device.id]) {
                    this.deviceState[device.id] = {
                        alias: device.alias || '',
                        hidden: device.hidden || false
                    };
                }
            });

            // Re-render the identify step with the loaded devices
            const content = document.getElementById('wizardContent');
            if (content && this.STEPS[this.currentStep].id === 'identify') {
                content.innerHTML = this.renderIdentify();
            }
        } catch (error) {
            console.error('Failed to load devices:', error);
            const container = document.getElementById('wizardIdentifyList');
            if (container) {
                container.innerHTML = `
                    <div class="alert alert-danger">
                        <i class="fas fa-exclamation-triangle me-2"></i>
                        Failed to load devices. Please try again.
                    </div>
                `;
            }
        }
    },

    // Refresh devices and re-render the identify step
    async refreshDevicesForIdentify() {
        const container = document.getElementById('wizardIdentifyList');
        if (container) {
            container.innerHTML = `
                <div class="text-center py-4">
                    <div class="spinner-border text-primary" role="status">
                        <span class="visually-hidden">Loading...</span>
                    </div>
                    <p class="text-muted mt-2">Refreshing devices...</p>
                </div>
            `;
        }

        try {
            await fetch('./api/devices/refresh', { method: 'POST' });
            this.devices = [];  // Clear to trigger reload
            await this.loadDevicesForIdentify();
        } catch (error) {
            console.error('Failed to refresh devices:', error);
        }
    },

    // Play test tone on device
    async playTestTone(deviceId) {
        const btn = document.getElementById(`tone-btn-${deviceId}`);
        if (!btn) return;

        const originalContent = btn.innerHTML;
        btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>Playing...';
        btn.disabled = true;
        btn.classList.add('playing');

        try {
            const response = await fetch(`./api/devices/${encodeURIComponent(deviceId)}/test-tone`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ frequencyHz: 1000, durationMs: 1500 })
            });

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.message || 'Failed to play test tone');
            }
        } catch (error) {
            console.error('Failed to play test tone:', error);
            showAlert(`Test tone failed: ${error.message}`, 'danger');
        } finally {
            btn.innerHTML = originalContent;
            btn.disabled = false;
            btn.classList.remove('playing');
        }
    },

    // Toggle device hidden state
    async toggleHidden(deviceId) {
        if (!this.deviceState[deviceId]) {
            this.deviceState[deviceId] = { alias: '', hidden: false };
        }
        const newHidden = !this.deviceState[deviceId].hidden;
        this.deviceState[deviceId].hidden = newHidden;

        // Persist to server
        try {
            await fetch(`./api/devices/${encodeURIComponent(deviceId)}/hidden`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ hidden: newHidden })
            });
        } catch (error) {
            console.error('Failed to save hidden state:', error);
        }

        // Re-render identify step to update UI
        if (this.STEPS[this.currentStep].id === 'identify') {
            const content = document.getElementById('wizardContent');
            if (content) {
                content.innerHTML = this.renderIdentify();
            }
        }
    },

    // Set device alias
    setAlias(deviceId, alias) {
        if (!this.deviceState[deviceId]) {
            this.deviceState[deviceId] = { alias: '', hidden: false };
        }
        this.deviceState[deviceId].alias = alias;
    },

    // Suggest a name for a device
    suggestName(device) {
        // Use alias if available
        if (device.alias) return device.alias;

        // Try to create a friendly name from the device name
        let name = device.name || device.id;

        // Remove common prefixes/suffixes
        name = name.replace(/^alsa_output\./, '');
        name = name.replace(/\.analog-stereo$/, '');
        name = name.replace(/\.iec958-stereo$/, '');
        name = name.replace(/usb-|pci-/gi, '');

        // Capitalize first letter of each word
        name = name.split(/[-_.]/).map(word =>
            word.charAt(0).toUpperCase() + word.slice(1).toLowerCase()
        ).join(' ');

        return name.substring(0, 30);  // Limit length
    },

    // Save aliases to server
    async saveAliases() {
        for (const [deviceId, data] of Object.entries(this.deviceState)) {
            if (data.alias) {
                try {
                    await fetch(`./api/devices/${encodeURIComponent(deviceId)}/alias`, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ alias: data.alias })
                    });
                } catch (error) {
                    console.error(`Failed to save alias for ${deviceId}:`, error);
                }
            }
        }
    },

    // Collect players to create from form
    collectPlayersToCreate() {
        this.playersToCreate = [];
        const checkboxes = document.querySelectorAll('[id^="create-player-"]:checked');

        checkboxes.forEach(checkbox => {
            const deviceId = checkbox.dataset.deviceId;
            const deviceName = checkbox.dataset.deviceName;
            const volumeInput = document.getElementById(`volume-${deviceId}`);
            const volume = volumeInput ? parseInt(volumeInput.value) : 75;

            this.playersToCreate.push({
                name: deviceName,
                device: deviceId,
                volume: volume,
                autostart: true
            });
        });
    },

    // Complete the wizard
    async complete() {
        try {
            // Create players
            if (this.playersToCreate.length > 0) {
                const response = await fetch('./api/onboarding/create-players', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ players: this.playersToCreate })
                });

                const result = await response.json();
                if (result.failedCount > 0) {
                    console.warn('Some players failed to create:', result.failed);
                }
            }

            // Mark onboarding as complete
            await fetch('./api/onboarding/complete', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    devicesConfigured: Object.keys(this.deviceState).length,
                    playersCreated: this.playersToCreate.length
                })
            });

            this.hide();

            // Refresh the main page
            if (typeof refreshStatus === 'function') {
                await refreshStatus();
            }
            if (typeof refreshDevices === 'function') {
                await refreshDevices();
            }

            showAlert(`Setup complete! Created ${this.playersToCreate.length} player(s).`, 'success');
        } catch (error) {
            console.error('Failed to complete onboarding:', error);
            showAlert('Failed to complete setup. Please try again.', 'danger');
        }
    }
};

// Initialize wizard when DOM is ready
document.addEventListener('DOMContentLoaded', () => {
    Wizard.init();
});

// Export as OnboardingWizard for app.js integration
const OnboardingWizard = {
    start: () => Wizard.show(),
    skip: () => Wizard.skip(),
    isActive: () => Wizard.modal && Wizard.modal._isShown
};
