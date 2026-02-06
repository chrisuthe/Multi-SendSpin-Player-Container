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
