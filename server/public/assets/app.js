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
