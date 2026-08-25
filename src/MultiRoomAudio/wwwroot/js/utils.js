// ========== Shared Modal Utilities ==========
// These functions provide styled Bootstrap modals to replace native browser dialogs

// Show a styled confirmation modal (returns a Promise that resolves to true/false)
function showConfirm(title, message, okText = 'OK', okClass = 'btn-danger') {
    return new Promise((resolve) => {
        const modal = document.getElementById('confirmModal');
        const titleEl = document.getElementById('confirmModalTitle');
        const messageEl = document.getElementById('confirmModalMessage');
        const okBtn = document.getElementById('confirmModalOkBtn');

        titleEl.textContent = title;
        messageEl.textContent = message;
        okBtn.textContent = okText;
        okBtn.className = `btn ${okClass}`;

        const bsModal = new bootstrap.Modal(modal);

        // Clean up any previous handlers
        const newOkBtn = okBtn.cloneNode(true);
        okBtn.parentNode.replaceChild(newOkBtn, okBtn);

        let resolved = false;

        newOkBtn.addEventListener('click', () => {
            resolved = true;
            bsModal.hide();
            resolve(true);
        });

        modal.addEventListener('hidden.bs.modal', function handler() {
            modal.removeEventListener('hidden.bs.modal', handler);
            if (!resolved) {
                resolve(false);
            }
        });

        bsModal.show();
    });
}

// Show a styled prompt modal for text input (returns a Promise that resolves to the input value or null)
function showPrompt(title, label, defaultValue = '', placeholder = '') {
    return new Promise((resolve) => {
        const modal = document.getElementById('promptModal');
        const titleEl = document.getElementById('promptModalTitle');
        const labelEl = document.getElementById('promptModalLabel');
        const inputEl = document.getElementById('promptModalInput');
        const okBtn = document.getElementById('promptModalOkBtn');

        titleEl.textContent = title;
        labelEl.textContent = label;
        inputEl.value = defaultValue;
        inputEl.placeholder = placeholder;

        const bsModal = new bootstrap.Modal(modal);

        // Clean up any previous handlers
        const newOkBtn = okBtn.cloneNode(true);
        okBtn.parentNode.replaceChild(newOkBtn, okBtn);

        let resolved = false;

        const submitValue = () => {
            resolved = true;
            bsModal.hide();
            resolve(inputEl.value);
        };

        newOkBtn.addEventListener('click', submitValue);

        // Allow Enter key to submit
        const keyHandler = (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                submitValue();
            }
        };
        inputEl.addEventListener('keydown', keyHandler);

        modal.addEventListener('hidden.bs.modal', function handler() {
            modal.removeEventListener('hidden.bs.modal', handler);
            inputEl.removeEventListener('keydown', keyHandler);
            if (!resolved) {
                resolve(null);
            }
        });

        bsModal.show();

        // Focus the input after modal is shown
        modal.addEventListener('shown.bs.modal', function focusHandler() {
            modal.removeEventListener('shown.bs.modal', focusHandler);
            inputEl.focus();
            inputEl.select();
        });
    });
}

// ========== Remap Sink Naming ==========
// Shared by the main app's Create Remap Sink modal and the onboarding wizard so both
// generate identical defaults.

// PulseAudio channel value -> friendly label / short name-suffix abbreviation
const REMAP_CHANNEL_INFO = {
    'front-left': { label: 'Front Left', abbrev: 'fl' },
    'front-right': { label: 'Front Right', abbrev: 'fr' },
    'front-center': { label: 'Front Center', abbrev: 'fc' },
    'lfe': { label: 'LFE', abbrev: 'lfe' },
    'rear-left': { label: 'Rear Left', abbrev: 'rl' },
    'rear-right': { label: 'Rear Right', abbrev: 'rr' },
    'side-left': { label: 'Side Left', abbrev: 'sl' },
    'side-right': { label: 'Side Right', abbrev: 'sr' }
};

// Longest name and description RemapSinkCreateRequest will accept
const REMAP_SINK_NAME_MAX = 100;
const REMAP_SINK_DESC_MAX = 200;

// Reduce a device description to the character set a sink name allows.
function slugifySinkName(text) {
    return (text || '')
        .replace(/[^a-zA-Z0-9]+/g, '_')
        .replace(/^_+|_+$/g, '');
}

// Build the name/description a remap sink gets by default.
// masterLabel: the master device's display name (e.g. "Creative X-Fi Analog Surround 7.1")
// channels:    selected master channel values, in output order (one entry for mono)
// existingNames: names already taken, used to pick a non-colliding suffix
function buildRemapSinkDefaults(masterLabel, channels, existingNames = []) {
    const picked = (channels || []).filter(Boolean);
    if (!masterLabel || picked.length === 0) {
        return { name: '', description: '' };
    }

    const labels = picked.map(ch => REMAP_CHANNEL_INFO[ch]?.label || ch);
    const description = `${masterLabel} ${labels.join(' ')}`.slice(0, REMAP_SINK_DESC_MAX).trim();

    const channelSuffix = picked.map(ch => REMAP_CHANNEL_INFO[ch]?.abbrev || slugifySinkName(ch)).join('_');
    const taken = new Set(existingNames);

    // The channel suffix is what makes the name meaningful, so the device slug is what
    // gives way when the 100-char cap bites.
    const compose = (ordinal) => {
        const tail = ordinal > 1 ? `_${channelSuffix}_${ordinal}` : `_${channelSuffix}`;
        let slug = slugifySinkName(masterLabel) || 'remap';
        if (slug.length + tail.length > REMAP_SINK_NAME_MAX) {
            slug = slug.slice(0, Math.max(1, REMAP_SINK_NAME_MAX - tail.length)).replace(/_+$/, '') || 'remap';
        }
        return `${slug}${tail}`.slice(0, REMAP_SINK_NAME_MAX);
    };

    let ordinal = 1;
    let name = compose(ordinal);
    while (taken.has(name) && ordinal < 100) {
        name = compose(++ordinal);
    }

    return { name, description };
}

// ========== Duplicate Device Disambiguation ==========

// Build a label function that appends "(card N)" only to labels shared by more than one
// entry, so identical sound cards stay tellable apart without noise on unique ones.
// getCardNumber should prefer the ALSA card number and fall back to the PulseAudio index.
function makeCardDisambiguator(items, getLabel, getCardNumber) {
    const counts = new Map();
    (items || []).forEach(item => {
        const label = getLabel(item);
        counts.set(label, (counts.get(label) || 0) + 1);
    });

    return (item) => {
        const label = getLabel(item);
        if ((counts.get(label) || 0) < 2) return label;
        const cardNumber = getCardNumber(item);
        if (cardNumber === null || cardNumber === undefined || cardNumber === '') return label;
        return `${label} (card ${cardNumber})`;
    };
}
