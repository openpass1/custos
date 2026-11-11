// CUSTOS Monitor: small helpers for the pages. No build step, no dependencies.
'use strict';

document.addEventListener('submit', (event) => {
    const form = event.target;
    const question = form.getAttribute('data-confirm');
    if (question && !window.confirm(question)) {
        event.preventDefault();
    }
});

document.addEventListener('click', async (event) => {
    const button = event.target.closest('[data-copy]');
    if (!button) {
        return;
    }
    const field = document.getElementById(button.getAttribute('data-copy'));
    if (!field) {
        return;
    }
    field.select();
    try {
        await navigator.clipboard.writeText(field.value);
    } catch {
        // Plain HTTP pages have no clipboard API; the selected text can still be copied with Ctrl+C.
        document.execCommand('copy');
    }
    const text = button.textContent;
    button.textContent = '✓';
    setTimeout(() => { button.textContent = text; }, 1500);
});

// A select with data-autosubmit sends its form as soon as the choice changes.
document.addEventListener('change', (event) => {
    const select = event.target.closest('select[data-autosubmit]');
    if (select && select.form) {
        select.form.requestSubmit ? select.form.requestSubmit() : select.form.submit();
    }
});

// Live parts: a [data-live] block asks its URL every data-live-every seconds and replaces each
// [data-part] inside it with the HTML that comes back; the bell count is refreshed too.
// A page whose session has ended is reloaded so that the login form appears.
(() => {
    const block = document.querySelector('[data-live]');
    if (!block) {
        return;
    }
    const url = block.getAttribute('data-live');
    const every = Math.max(5, parseInt(block.getAttribute('data-live-every'), 10) || 15) * 1000;
    let busy = false;

    const refresh = async () => {
        if (busy || document.hidden) {
            return;
        }
        busy = true;
        try {
            const response = await fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin', cache: 'no-store' });
            if (response.status === 401) {
                window.location.reload();
                return;
            }
            if (!response.ok) {
                return;
            }
            const data = await response.json();
            for (const [name, html] of Object.entries(data.parts || {})) {
                const part = block.querySelector('[data-part="' + name + '"]');
                // A form being filled in (or with focus inside) is left alone until the next round.
                if (part && !part.contains(document.activeElement)) {
                    part.innerHTML = html;
                }
            }
            if (typeof data.bell === 'number') {
                for (const bell of document.querySelectorAll('[data-bell]')) {
                    bell.textContent = String(data.bell);
                    bell.hidden = data.bell === 0;
                }
            }
        } catch {
            // The server is unreachable for a moment: try again on the next round.
        } finally {
            busy = false;
        }
    };

    setInterval(refresh, every);
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) {
            refresh();
        }
    });
})();
