// CUSTOS Monitor: small helpers for the pages. No build step, no dependencies.
'use strict';

// Less motion: Windows with its animation effects off, or "reduce motion" on another system. app.css then shows
// everything without movement; scripts skip their own movement too, and nothing waits for an animation to end.
const stillQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
const still = () => stillQuery.matches;

// A quiet note at the bottom of the page says why the site is so still, and on Windows where to switch effects on.
(() => {
    const note = document.querySelector('[data-motion-note]');
    if (!note) {
        return;
    }
    if (/Windows/.test(navigator.userAgent)) {
        note.textContent = note.getAttribute('data-windows') || note.textContent;
        note.title = note.getAttribute('data-windows-hint') || note.title;
    }
    const show = () => {
        note.hidden = !still();
    };
    show();
    if (typeof stillQuery.addEventListener === 'function') {
        stillQuery.addEventListener('change', show);
    }
})();

// A form with data-confirm asks first, in the site's own dialog (#confirm-dialog, layout.php): the first sentence is the
// question, the rest explains, and the button repeats the action ("Delete machine"); for a risky action it is red and
// Cancel has the focus. Where the dialog is missing (wall screen, an old browser) the browser asks instead.
(() => {
    const dialog = document.getElementById('confirm-dialog');
    let pending = null;

    const send = (form, submitter) => {
        form.setAttribute('data-confirmed', '');
        if (typeof form.requestSubmit === 'function') {
            form.requestSubmit(submitter && submitter.form === form ? submitter : undefined);
        } else {
            form.submit();
        }
    };

    const ask = (form, submitter, question) => {
        const m = /^(.*?[?？])\s+([\s\S]+)$/.exec(question.trim());
        const title = dialog.querySelector('#confirm-title');
        const text = dialog.querySelector('#confirm-text');
        const ok = dialog.querySelector('[data-confirm-ok]');
        const cancel = dialog.querySelector('[data-confirm-cancel]');
        title.textContent = m ? m[1] : question;
        text.textContent = m ? m[2] : '';
        text.hidden = !m;
        const risky = !!submitter && submitter.classList.contains('danger');
        ok.textContent = (submitter && submitter.textContent.trim()) || ok.getAttribute('data-default') || 'OK';
        ok.className = risky ? 'danger-fill' : '';
        // The menu the question came from closes; the focus comes back to its button afterwards.
        const menu = form.closest('details.dropdown');
        if (menu) {
            menu.open = false;
        }
        pending = { form, submitter, back: menu ? menu.querySelector('summary') : submitter };
        dialog.showModal();
        (risky ? cancel : ok).focus();
    };

    if (dialog) {
        dialog.querySelector('[data-confirm-ok]').addEventListener('click', () => {
            const job = pending;
            pending = null;
            dialog.close();
            if (job) {
                send(job.form, job.submitter);
            }
        });
        dialog.querySelector('[data-confirm-cancel]').addEventListener('click', () => dialog.close());
        // Cancel, Esc and a click outside all end here.
        dialog.addEventListener('close', () => {
            if (pending) {
                const back = pending.back;
                pending = null;
                if (back && back.isConnected) {
                    back.focus();
                }
            }
        });
    }

    document.addEventListener('submit', (event) => {
        const form = event.target;
        const question = form.getAttribute('data-confirm');
        if (!question) {
            return;
        }
        if (form.hasAttribute('data-confirmed')) {
            // Asked and answered: this is the sending itself. The next sending asks again.
            form.removeAttribute('data-confirmed');
            return;
        }
        event.preventDefault();
        if (dialog && typeof dialog.showModal === 'function') {
            ask(form, event.submitter || null, question);
        } else if (window.confirm(question)) {
            send(form, event.submitter || null);
        }
    });
})();

// A form on its way: the button shows that the site is working, and a second click does not send the form again.
// Back to the page from the browser history, or no answer within 10 seconds (a file download): the button is ready again.
(() => {
    const ready = (form) => {
        form.removeAttribute('data-sending');
        for (const button of form.querySelectorAll('.busy')) {
            button.classList.remove('busy');
            button.removeAttribute('aria-busy');
        }
    };
    document.addEventListener('submit', (event) => {
        const form = event.target;
        if (event.defaultPrevented) {
            return;
        }
        if (form.hasAttribute('data-sending')) {
            event.preventDefault();
            return;
        }
        form.setAttribute('data-sending', '');
        const button = event.submitter || form.querySelector('button[type="submit"], button:not([type])');
        if (button) {
            button.classList.add('busy');
            button.setAttribute('aria-busy', 'true');
        }
        setTimeout(() => ready(form), 10000);
    });
    window.addEventListener('pageshow', () => {
        for (const form of document.querySelectorAll('form[data-sending]')) {
            ready(form);
        }
    });
})();

// Messages after an action (layout.php, .toasts) float over the page: a plain one goes away by itself after a time long
// enough to read it (the pointer over it holds it), an error stays until its × is pressed.
(() => {
    const box = document.querySelector('.toasts');
    if (!box) {
        return;
    }
    const label = box.getAttribute('data-close-label') || '×';
    const close = (toast) => {
        if (toast.classList.contains('leaving')) {
            return;
        }
        toast.classList.add('leaving');
        setTimeout(() => {
            toast.remove();
            if (!box.querySelector('.flash')) {
                box.remove();
            }
        }, still() ? 0 : 200);
    };
    for (const toast of box.querySelectorAll('.flash')) {
        const error = toast.classList.contains('flash-error');
        toast.setAttribute('role', error ? 'alert' : 'status');
        const x = document.createElement('button');
        x.type = 'button';
        x.className = 'toast-close';
        x.title = label;
        x.setAttribute('aria-label', label);
        x.innerHTML = '<svg class="icon" aria-hidden="true" focusable="false"><use href="#i-x"/></svg>';
        x.addEventListener('click', () => close(toast));
        toast.append(x);
        if (error) {
            continue;
        }
        let timer = 0;
        const wait = (ms) => {
            clearTimeout(timer);
            timer = setTimeout(() => close(toast), ms);
        };
        wait(Math.min(12000, 3500 + toast.textContent.length * 45));
        toast.addEventListener('mouseenter', () => clearTimeout(timer));
        toast.addEventListener('mouseleave', () => wait(2500));
    }
})();

// A table with class row-links opens the first link of a row wherever the row is clicked (Ctrl: in a new tab).
// Links, buttons and menus in the row keep their own job; selecting text does not open anything.
document.addEventListener('click', (event) => {
    const row = event.target.closest('table.row-links tbody tr');
    if (!row || event.button !== 0 || event.target.closest('a, button, input, select, textarea, label, summary, details, form, .row-actions')) {
        return;
    }
    if (String(window.getSelection() || '') !== '') {
        return;
    }
    const link = row.querySelector('a[href]');
    if (!link) {
        return;
    }
    if (event.ctrlKey || event.metaKey || event.shiftKey) {
        window.open(link.href, '_blank', 'noopener');
    } else {
        window.location.href = link.href;
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
    // The button answers in words for a moment: "✓ Copied" (data-copied), then reads as before.
    if (button.hasAttribute('data-copying')) {
        return;
    }
    const before = button.innerHTML;
    button.setAttribute('data-copying', '');
    button.textContent = '✓ ' + (button.getAttribute('data-copied') || '');
    setTimeout(() => {
        button.innerHTML = before;
        button.removeAttribute('data-copying');
    }, 1600);
});

// "More" menus (details.dropdown): one open at a time, closed by a click elsewhere or Esc. A menu inside a table that
// scrolls sideways is placed over the page (position: fixed), so that the edge of the table does not cut it off; it
// opens upwards when there is no room below. Any menu opens rightwards when there is no room on the left (phones).
(() => {
    const closeAll = (except) => {
        for (const open of document.querySelectorAll('details.dropdown[open]')) {
            if (open !== except && !open.contains(except)) {
                open.open = false;
            }
        }
    };
    const place = (dropdown) => {
        const menu = dropdown.querySelector('.dropdown-menu');
        const summary = dropdown.querySelector('summary');
        if (!menu || !summary) {
            return;
        }
        menu.style.position = '';
        menu.style.top = '';
        menu.style.bottom = '';
        menu.style.right = '';
        menu.style.left = '';
        if (dropdown.closest('header.top')) {
            return; // app.css places the menus of the top bar
        }
        if (!dropdown.closest('.table-wrap')) {
            // The menu hangs from the right edge of its button; a button at the left of a narrow screen opens it rightwards.
            if (menu.getBoundingClientRect().left < 8) {
                menu.style.right = 'auto';
                menu.style.left = '0';
            }
            return;
        }
        const box = summary.getBoundingClientRect();
        menu.style.position = 'fixed';
        menu.style.right = Math.max(8, document.documentElement.clientWidth - box.right) + 'px';
        if (box.bottom + menu.offsetHeight + 12 > window.innerHeight && box.top > menu.offsetHeight + 12) {
            menu.style.top = (box.top - menu.offsetHeight - 6) + 'px';
        } else {
            menu.style.top = (box.bottom + 6) + 'px';
        }
    };
    // "toggle" does not bubble: listen while it goes down.
    document.addEventListener('toggle', (event) => {
        const dropdown = event.target;
        if (dropdown instanceof HTMLDetailsElement && dropdown.classList.contains('dropdown') && dropdown.open) {
            closeAll(dropdown);
            place(dropdown);
        }
    }, true);
    document.addEventListener('click', (event) => {
        if (!event.target.closest('details.dropdown')) {
            closeAll(null);
        }
    });
    document.addEventListener('keydown', (event) => {
        if (event.key !== 'Escape') {
            return;
        }
        const open = document.querySelector('details.dropdown[open]');
        if (open) {
            open.open = false;
            const summary = open.querySelector('summary');
            if (summary) {
                summary.focus();
            }
        }
    });
    // A fixed menu would stay where it was while the page moves under it.
    window.addEventListener('scroll', () => {
        for (const open of document.querySelectorAll('.table-wrap details.dropdown[open]')) {
            open.open = false;
        }
    }, { passive: true });
})();

// Small dialogs (rename a machine, change its group): [data-dialog="id"] opens <dialog id="id">, [data-dialog-close]
// or a click outside the dialog closes it. The first field gets the focus.
document.addEventListener('click', (event) => {
    const opener = event.target.closest('[data-dialog]');
    if (opener) {
        const dialog = document.getElementById(opener.getAttribute('data-dialog'));
        if (dialog && typeof dialog.showModal === 'function') {
            const menu = opener.closest('details.dropdown');
            if (menu) {
                menu.open = false;
            }
            dialog.showModal();
            const field = dialog.querySelector('input:not([type="hidden"]), select, textarea');
            if (field) {
                field.focus();
                if (typeof field.select === 'function' && field.tagName === 'INPUT') {
                    field.select();
                }
            }
        }
        return;
    }
    const closer = event.target.closest('[data-dialog-close]');
    if (closer && closer.closest('dialog')) {
        closer.closest('dialog').close();
        return;
    }
    if (event.target instanceof HTMLDialogElement && event.target.open) {
        const box = event.target.getBoundingClientRect();
        const inside = event.clientX >= box.left && event.clientX <= box.right && event.clientY >= box.top && event.clientY <= box.bottom;
        if (!inside) {
            event.target.close();
        }
    }
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

    // What changed is shown for a moment: a row whose state changed ([data-key] with a new data-sig) lights up, a new
    // one (a problem, a run) slides in, and a progress bar glides to its new value instead of jumping.
    const flag = (el, name, ms = 1600) => {
        el.classList.add(name);
        setTimeout(() => el.classList.remove(name), ms);
    };
    const snapshot = (part) => {
        const map = new Map();
        for (const el of part.querySelectorAll('[data-key]')) {
            map.set(el.getAttribute('data-key'), {
                sig: el.getAttribute('data-sig'),
                values: [...el.querySelectorAll('progress')].map((bar) => bar.value),
            });
        }
        return map;
    };
    const glide = (bar, from) => {
        const to = bar.value;
        if (still() || !(to > from)) {
            return;
        }
        const start = performance.now();
        bar.value = from;
        const step = (now) => {
            const k = Math.min(1, (now - start) / 900);
            bar.value = from + (to - from) * (1 - Math.pow(1 - k, 3));
            if (k < 1 && bar.isConnected) {
                window.requestAnimationFrame(step);
            }
        };
        window.requestAnimationFrame(step);
    };
    const mark = (part, before) => {
        for (const el of part.querySelectorAll('[data-key]')) {
            const old = before.get(el.getAttribute('data-key'));
            if (!old) {
                flag(el, 'arrived');
                continue;
            }
            if (old.sig !== null && old.sig !== el.getAttribute('data-sig')) {
                flag(el, 'changed');
            }
            el.querySelectorAll('progress').forEach((bar, i) => {
                if (old.values[i] !== undefined) {
                    glide(bar, old.values[i]);
                }
            });
        }
    };

    // The wall screen says so when the data it shows is getting old.
    const offline = (on) => {
        for (const mark of block.querySelectorAll('[data-live-offline]')) {
            mark.hidden = !on;
        }
    };

    const refresh = async () => {
        if (busy || document.hidden) {
            return;
        }
        busy = true;
        try {
            const response = await fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin', cache: 'no-store' });
            // 401: the session has ended; 403 on the wall screen: its token was replaced. The page shows why.
            if (response.status === 401 || (response.status === 403 && block.hasAttribute('data-live-public'))) {
                window.location.reload();
                return;
            }
            if (!response.ok) {
                offline(true);
                return;
            }
            offline(false);
            const data = await response.json();
            for (const [name, html] of Object.entries(data.parts || {})) {
                const part = block.querySelector('[data-part="' + name + '"]');
                // A form being filled in (or with focus inside) and an open menu are left alone until the next round.
                if (part && !part.contains(document.activeElement) && !part.querySelector('details.dropdown[open]')) {
                    const before = snapshot(part);
                    part.innerHTML = html;
                    mark(part, before);
                }
            }
            if (typeof data.bell === 'number') {
                for (const bell of document.querySelectorAll('[data-bell]')) {
                    const was = parseInt(bell.textContent, 10) || 0;
                    bell.textContent = String(data.bell);
                    bell.hidden = data.bell === 0;
                    if (data.bell > was) {
                        flag(bell, 'bump', 700);
                    }
                }
            }
        } catch {
            // The server is unreachable for a moment: try again on the next round.
            offline(true);
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

// Forms with conditional parts (task editor, agent settings, updates):
// - [data-show-if="name=a|b&other!=c"] is shown only while the fields of its scope have these values. The scope is
//   the nearest [data-scope]; inside a row of a list the names are the last part: "kind" stands for sources[3][kind].
// - [data-add-row="x"] adds a copy of <template data-row-template="x"> to [data-rows="x"]; [data-remove-row] removes
//   its row ([data-row]).
// - Hidden parts are left out of the browser's form check: a field of a hidden part (the timer interval of a daily
//   task) cannot show its message, so the browser would silently refuse to send the form. Visible fields are checked
//   as usual; the server checks everything again.
// - In the task editor the account list of a row shows only accounts of the kind the row needs, and 7z is offered
//   only while every destination can seek (client spec 6.5).
(() => {
    const fieldsOf = (scope, name) => [...scope.querySelectorAll('input[name], select[name], textarea[name]')]
        .filter((f) => f.closest('[data-scope]') === scope && (f.name === name || f.name.endsWith('[' + name + ']')));

    const valueOf = (scope, name) => {
        for (const field of fieldsOf(scope, name)) {
            if (field.type === 'radio' || field.type === 'checkbox') {
                if (field.checked) {
                    return field.value;
                }
            } else {
                return field.value;
            }
        }
        return '';
    };

    const holds = (scope, condition) => condition.split('&').every((part) => {
        const m = /^([^!=]+)(!?=)(.*)$/.exec(part.trim());
        if (!m) {
            return true;
        }
        const match = m[3].split('|').includes(valueOf(scope, m[1]));
        return m[2] === '=' ? match : !match;
    });

    const editorChecks = (editor) => {
        const seekable = (editor.getAttribute('data-seekable') || '').split('|');
        const kinds = [...editor.querySelectorAll('select[data-destination-kind]')].map((s) => s.value);
        const bad = kinds.some((k) => !seekable.includes(k));
        for (const radio of editor.querySelectorAll('input[data-needs-seekable]')) {
            // A 7z already chosen stays chosen (the server explains why it cannot be saved); it is not offered anew.
            radio.disabled = editor.disabled || (bad && !radio.checked);
        }
        for (const hint of editor.querySelectorAll('[data-seekable-hint]')) {
            hint.hidden = !bad;
        }
    };

    const filterAccounts = (editor, row, reset) => {
        let map = {};
        try {
            map = JSON.parse(editor.getAttribute('data-credential-kinds') || '{}');
        } catch {
            return;
        }
        const select = row.querySelector('select[data-credential-select]');
        if (!select) {
            return;
        }
        const need = map[valueOf(row, 'kind')] || '';
        for (const option of select.querySelectorAll('option[data-kind]')) {
            option.hidden = option.getAttribute('data-kind') !== need;
            if (option.hidden && option.selected && reset) {
                select.value = '';
            }
        }
    };

    const update = (changed) => {
        for (const el of document.querySelectorAll('[data-show-if]')) {
            const scope = el.parentElement ? el.parentElement.closest('[data-scope]') : null;
            if (scope) {
                const was = el.hidden;
                el.hidden = !holds(scope, el.getAttribute('data-show-if'));
                // A part that appears after a choice fades in, so the eye finds what was added.
                if (changed !== null && was && !el.hidden) {
                    el.classList.add('appearing');
                    setTimeout(() => el.classList.remove('appearing'), 400);
                }
            }
        }
        // [data-placeholders='{"folder": "D:\\Data", ...}']: the example in an empty field follows the kind of its row.
        for (const field of document.querySelectorAll('[data-placeholders]')) {
            const scope = field.closest('[data-scope]');
            try {
                const examples = JSON.parse(field.getAttribute('data-placeholders') || '{}');
                field.placeholder = (scope && examples[valueOf(scope, 'kind')]) || '';
            } catch {
                // A broken list leaves the example the page came with.
            }
        }
        for (const editor of document.querySelectorAll('[data-editor]')) {
            editorChecks(editor);
            for (const row of editor.querySelectorAll('[data-row]')) {
                filterAccounts(editor, row, changed !== null && row.contains(changed));
            }
        }
    };

    let counter = 0;
    document.addEventListener('click', (event) => {
        const add = event.target.closest('[data-add-row]');
        if (add) {
            const name = add.getAttribute('data-add-row');
            const template = document.querySelector('template[data-row-template="' + name + '"]');
            const rows = document.querySelector('[data-rows="' + name + '"]');
            if (template && rows) {
                const index = 'n' + Date.now().toString(36) + (counter++);
                rows.insertAdjacentHTML('beforeend', template.innerHTML.split('__i__').join(index));
                update(null);
                const first = rows.lastElementChild && rows.lastElementChild.querySelector('input[type="text"]');
                if (first) {
                    first.focus();
                }
            }
            return;
        }
        const remove = event.target.closest('[data-remove-row]');
        if (remove) {
            const row = remove.closest('[data-row]');
            if (row) {
                row.remove();
                update(null);
            }
        }
    });
    document.addEventListener('change', (event) => update(event.target));
    document.addEventListener('input', (event) => {
        if (event.target.matches('select, input[type="radio"], input[type="checkbox"]')) {
            update(event.target);
        }
    });
    // Capture phase: runs before the data-confirm question, so nothing is asked about a form that cannot be sent yet.
    document.addEventListener('submit', (event) => {
        const form = event.target;
        if (!form.hasAttribute('data-check-visible')) {
            return;
        }
        for (const field of form.elements) {
            if (!field.willValidate || field.closest('[hidden]') || field.checkValidity()) {
                continue;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            const folded = field.closest('details:not([open])');
            if (folded) {
                folded.open = true;
            }
            field.reportValidity();
            return;
        }
    }, true);

    if (document.querySelector('[data-show-if], [data-editor]')) {
        for (const el of document.querySelectorAll('[data-show-if]')) {
            const form = el.closest('form');
            if (form) {
                form.noValidate = true;
                form.setAttribute('data-check-visible', '');
            }
        }
        update(null);
    }
})();

// SMTP form: choosing SSL or STARTTLS fills in the usual port (465 or 587), unless a non-standard port was typed in;
// the SMTP login is copied into the sender address while the two are the same (usually they are). Both stay editable.
(() => {
    const block = document.querySelector('[data-smtp]');
    if (!block) {
        return;
    }
    const encryption = block.querySelector('[name="smtp_encryption"]');
    const port = block.querySelector('[name="smtp_port"]');
    const login = block.querySelector('[name="smtp_user"]');
    const from = block.querySelector('[name="mail_from"]');
    const ports = { ssl: '465', starttls: '587', none: '25' };
    if (encryption && port) {
        encryption.addEventListener('change', () => {
            const standard = port.value === '' || Object.values(ports).includes(port.value);
            if (standard && ports[encryption.value]) {
                port.value = ports[encryption.value];
            }
        });
    }
    if (login && from) {
        let same = from.value === '' || from.value === login.value;
        from.addEventListener('input', () => {
            same = from.value === '' || from.value === login.value;
        });
        login.addEventListener('input', () => {
            if (same) {
                from.value = login.value.includes('@') ? login.value : '';
            }
        });
    }
})();

// Charts of the task page (7.4): source size, written volume and duration of the latest runs, anomalies in red.
// Chart.js (public/assets/vendor) is loaded only on that page; the data comes in <script id="task-chart-data">.
// A click on a point opens that run.
document.addEventListener('DOMContentLoaded', () => {
    const holder = document.getElementById('task-chart-data');
    if (!holder || typeof window.Chart === 'undefined') {
        return;
    }
    let data;
    try {
        data = JSON.parse(holder.textContent);
    } catch {
        return;
    }
    const css = getComputedStyle(document.documentElement);
    const color = (name, fallback) => (css.getPropertyValue(name) || '').trim() || fallback;
    const text = data.text;
    const bytes = (value) => {
        if (value === null || value === undefined) {
            return '—';
        }
        let v = Number(value);
        let i = 0;
        while (v >= 1024 && i < text.units.length - 1) {
            v /= 1024;
            i++;
        }
        const number = i === 0 ? String(Math.round(v)) : v.toFixed(v < 10 ? 1 : 0).replace('.', text.decimal);
        return number + ' ' + text.units[i];
    };
    const minutes = (seconds) => seconds === null ? '—' : (Math.round(seconds / 6) / 10).toString().replace('.', text.decimal) + ' ' + text.minutes;
    const accent = color('--info', '#1f5fbf');
    const bad = color('--bad', '#b3261e');
    const muted = color('--muted', '#687083');
    const grid = color('--border', '#dde1e8');
    window.Chart.defaults.color = muted;
    window.Chart.defaults.font.family = css.fontFamily;
    const pointColors = data.anomaly.map((a, i) => a || data.status[i] === 'error' ? bad : accent);
    const pointSizes = data.anomaly.map((a) => a ? 6 : 2);

    const make = (canvas, kind, values, format) => new window.Chart(canvas, {
        type: kind,
        data: {
            labels: data.labels,
            datasets: [{
                data: values,
                borderColor: accent,
                backgroundColor: kind === 'bar' ? pointColors : accent,
                pointBackgroundColor: pointColors,
                pointBorderColor: pointColors,
                pointRadius: pointSizes,
                pointHoverRadius: 7,
                borderWidth: 2,
                tension: 0.2,
                spanGaps: true,
            }],
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            interaction: { mode: 'index', intersect: false },
            plugins: {
                legend: { display: false },
                tooltip: {
                    callbacks: {
                        label: (item) => {
                            const lines = [format(item.raw)];
                            if (canvas.getAttribute('data-chart') === 'source' && data.files[item.dataIndex] !== null) {
                                lines.push(text.files + ': ' + Number(data.files[item.dataIndex]).toLocaleString());
                            }
                            if (data.anomaly[item.dataIndex]) {
                                lines.push(text.anomaly);
                            }
                            return lines;
                        },
                    },
                },
            },
            scales: {
                // Days under the axis, the full time in the tooltip.
                x: { ticks: { maxTicksLimit: 8, maxRotation: 0, autoSkipPadding: 12, callback: (value) => data.days[value] }, grid: { display: false } },
                y: { beginAtZero: true, ticks: { callback: (value) => format(value), maxTicksLimit: 5 }, grid: { color: grid } },
            },
            onClick: (event, elements) => {
                if (elements.length > 0 && data.runs[elements[0].index]) {
                    window.location.href = data.runs[elements[0].index];
                }
            },
        },
    });

    for (const canvas of document.querySelectorAll('canvas[data-chart]')) {
        const which = canvas.getAttribute('data-chart');
        if (which === 'source') {
            make(canvas, 'line', data.source, bytes);
        } else if (which === 'written') {
            make(canvas, 'bar', data.written, bytes);
        } else if (which === 'duration') {
            make(canvas, 'line', data.duration, minutes);
        }
    }
});

// "Back to top" (layout.php): the button shows once the menu and the top lines of the page have scrolled away.
(() => {
    const SHOW_AFTER_PX = 300;
    const button = document.querySelector('[data-to-top]');
    if (!button) {
        return;
    }
    let pending = false;
    const update = () => {
        pending = false;
        button.classList.toggle('shown', window.scrollY > SHOW_AFTER_PX);
    };
    const schedule = () => {
        if (!pending) {
            pending = true;
            window.requestAnimationFrame(update);
        }
    };
    window.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule, { passive: true });
    update();
    button.addEventListener('click', () => {
        window.scrollTo({ top: 0, behavior: still() ? 'auto' : 'smooth' });
        // The button hides at the top; keyboard focus goes to the first link of the page instead of getting lost.
        const first = document.querySelector('header.top a');
        if (first) {
            first.focus({ preventScroll: true });
        }
    });
})();
