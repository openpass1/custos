// CUSTOS Monitor: small helpers for the pages. No build step, no dependencies.
'use strict';

// Less motion: Windows with its animation effects off, or "reduce motion" on another system. app.css then shows
// everything without movement; scripts skip their own movement too, and nothing waits for an animation to end.
const stillQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
const still = () => stillQuery.matches;

// The site's icons are symbols of one file (layout.php: <body data-icons>); scripts draw them the way icon() does.
const iconSvg = (name) => '<svg class="icon" aria-hidden="true" focusable="false"><use href="'
    + (document.body.getAttribute('data-icons') || '/assets/icons.svg') + '#i-' + name + '"/></svg>';

// The ring of a run in progress (helpers.php ring() and ring_dashes(), the same numbers): two circles with
// pathLength 100, the done part and the rest, a gap between them; a round cap adds half the stroke at each end.
const ringDashes = (p) => {
    const circumference = 2 * Math.PI * 9.5;
    const cap = 1.5 / circumference * 100;
    const gap = 2.5 / circumference * 100;
    return [Math.max(0.01, p - 2 * cap), cap, 100 - p - 2 * gap - 2 * cap, p + gap + cap];
};
const drawRing = (ring, p) => {
    const [valueLength, valueStart, trackLength, trackStart] = ringDashes(Math.max(0, Math.min(100, p)));
    const set = (circle, length, start, shown) => {
        if (!circle) {
            return;
        }
        circle.setAttribute('stroke-dasharray', length.toFixed(2) + ' 100');
        circle.setAttribute('stroke-dashoffset', (-start).toFixed(2));
        circle.setAttribute('visibility', shown ? 'visible' : 'hidden');
    };
    set(ring.querySelector('.ring-track'), trackLength, trackStart, trackLength > 0.5);
    set(ring.querySelector('.ring-value'), valueLength, valueStart, p > 0);
};

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
        x.innerHTML = iconSvg('x');
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

// Live parts: blocks of a page that show what agents and cron change carry data-part="name" and are put in place again
// every few seconds, so that a page left open shows a new machine, the tasks it sent, a finished run or a change the
// agent confirmed without F5.
// - <main data-live-page="10"> (layout.php, 'live' => 10 in the controller): the page asks for itself again (header
//   X-Custos-Live) and takes the parts with the same names from the answer, and the counters of the menu and the bell.
// - [data-live="url"] with data-live-every (dashboard, wall screen): the URL answers {parts: {name: html}, bell, counts}.
// A part is left as it is for a round while it is in use: a field of it has the focus or was changed (a form being
// filled in keeps what was typed until it is sent), text in it is selected, a menu of it is open, a form of it is on
// its way, or a dialog is open over the page. A folded block (details) stays open or closed as the user left it.
// The requests do not keep the session alive; a page whose session has ended is reloaded so that the login appears.
// After a round that changed something, "custos:parts" goes to the document for the helpers below.
(() => {
    const json = document.querySelector('[data-live]');
    const block = json || document.querySelector('[data-live-page]');
    if (!block) {
        return;
    }
    const url = json ? json.getAttribute('data-live') : null;
    const seconds = parseInt(json ? block.getAttribute('data-live-every') : block.getAttribute('data-live-page'), 10);
    const every = Math.max(5, seconds || 15) * 1000;
    const scope = json ? block : document;
    const last = new WeakMap(); // the HTML each part got last time, to leave a part alone when nothing changed
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
                rings: [...el.querySelectorAll('[data-ring]')].map((ring) => parseFloat(ring.getAttribute('data-ring')) || 0),
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
    // A ring glides the same way.
    const glideRing = (ring, from) => {
        const to = parseFloat(ring.getAttribute('data-ring')) || 0;
        if (still() || !(to > from)) {
            return;
        }
        const start = performance.now();
        const step = (now) => {
            const k = Math.min(1, (now - start) / 900);
            drawRing(ring, from + (to - from) * (1 - Math.pow(1 - k, 3)));
            if (k < 1 && ring.isConnected) {
                window.requestAnimationFrame(step);
            }
        };
        drawRing(ring, from);
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
            el.querySelectorAll('[data-ring]').forEach((ring, i) => {
                if (old.rings[i] !== undefined) {
                    glideRing(ring, old.rings[i]);
                }
            });
        }
    };

    // A field changed by hand keeps its part as it is until the page is sent or opened again.
    const touch = (event) => {
        const field = event.target;
        if (field instanceof Element && field.matches('input, select, textarea')) {
            const part = field.closest('[data-part]');
            if (part) {
                part.setAttribute('data-touched', '');
            }
        }
    };
    document.addEventListener('input', touch);
    document.addEventListener('change', touch);

    const inUse = (part) => {
        const focus = document.activeElement;
        if (part.hasAttribute('data-touched')
            || (focus && part.contains(focus) && focus.matches('input, select, textarea, [contenteditable]'))
            || part.querySelector('details.dropdown[open], form[data-sending]')) {
            return true;
        }
        const selection = window.getSelection();
        return !!selection && !selection.isCollapsed && selection.rangeCount > 0
            && part.contains(selection.getRangeAt(0).commonAncestorContainer);
    };

    const folds = (part) => [...part.querySelectorAll('details:not(.dropdown)')];
    const foldKey = (el, i) => el.id || el.getAttribute('data-key') || String(i);
    const put = (part, html) => {
        if (last.has(part) ? last.get(part) === html : part.innerHTML === html) {
            last.set(part, html);
            return false;
        }
        const open = new Map(folds(part).map((el, i) => [foldKey(el, i), el.open]));
        const before = snapshot(part);
        part.innerHTML = html;
        last.set(part, html);
        folds(part).forEach((el, i) => {
            const was = open.get(foldKey(el, i));
            if (was !== undefined && was !== el.open) {
                el.open = was;
            }
        });
        mark(part, before);
        return true;
    };

    const counters = (bell, counts) => {
        if (typeof bell === 'number') {
            for (const el of document.querySelectorAll('[data-bell]')) {
                const was = parseInt(el.textContent, 10) || 0;
                el.textContent = String(bell);
                el.hidden = bell === 0;
                if (bell > was) {
                    flag(el, 'bump', 700);
                }
            }
        }
        for (const [key, n] of Object.entries(counts || {})) {
            for (const el of document.querySelectorAll('[data-nav-count="' + key + '"]')) {
                const was = parseInt(el.textContent, 10) || 0;
                el.textContent = String(n);
                el.hidden = n === 0;
                if (n > was) {
                    flag(el, 'bump', 700);
                }
            }
        }
    };

    // The wall screen says so when the data it shows is getting old.
    const offline = (on) => {
        for (const el of block.querySelectorAll('[data-live-offline]')) {
            el.hidden = !on;
        }
    };

    // The fresh parts, the bell and the menu counters: from the JSON of the live URL, or from the page itself.
    const fetchFresh = async () => {
        const response = await fetch(url || window.location.href, {
            headers: url ? { 'Accept': 'application/json' } : { 'X-Custos-Live': '1' },
            credentials: 'same-origin',
            cache: 'no-store',
        });
        // 401: the session has ended; 403 on the wall screen: its token was replaced; 503 with X-Custos-Maintenance:
        // the site is being updated. The page shows why (the update page reloads itself until the site is back).
        if (response.status === 401 || (response.status === 403 && block.hasAttribute('data-live-public'))
            || (response.status === 503 && response.headers.get('X-Custos-Maintenance'))) {
            window.location.reload();
            return null;
        }
        if (!response.ok) {
            throw new Error('HTTP ' + response.status);
        }
        if (url) {
            return response.json();
        }
        // Somewhere else now (the machine or task was deleted): the page stays as it is.
        if (response.redirected) {
            return {};
        }
        const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
        // The site was updated meanwhile: the whole page, with the new scripts and styles.
        const version = doc.querySelector('footer[data-version]');
        const shown = document.querySelector('footer[data-version]');
        if (version && shown && version.getAttribute('data-version') !== shown.getAttribute('data-version')) {
            window.location.reload();
            return null;
        }
        const parts = {};
        for (const el of doc.querySelectorAll('[data-part]')) {
            parts[el.getAttribute('data-part')] = el.innerHTML;
        }
        const bell = doc.querySelector('[data-bell]');
        const counts = {};
        for (const el of doc.querySelectorAll('[data-nav-count]')) {
            counts[el.getAttribute('data-nav-count')] = parseInt(el.textContent, 10) || 0;
        }
        return { parts, bell: bell ? parseInt(bell.textContent, 10) || 0 : undefined, counts };
    };

    const refresh = async () => {
        if (busy || document.hidden) {
            return;
        }
        busy = true;
        try {
            const data = await fetchFresh();
            if (data === null) {
                return;
            }
            offline(false);
            // A question or a small form open over the page: its button may belong to a part, so nothing moves.
            if (document.querySelector('dialog[open]')) {
                return;
            }
            let changed = false;
            for (const [name, html] of Object.entries(data.parts || {})) {
                const part = scope.querySelector('[data-part="' + name + '"]');
                if (part && !inUse(part) && put(part, html)) {
                    changed = true;
                }
            }
            counters(data.bell, data.counts);
            if (changed) {
                document.dispatchEvent(new CustomEvent('custos:parts'));
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

    const init = () => {
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
    };
    init();
    // A live part may bring a form with conditional parts (agent settings, updates): it is set up the same way.
    document.addEventListener('custos:parts', init);
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

// Task editor (tasks/_form.php): the table of contents marks the section on screen and opens a folded one it jumps to;
// a field that the browser finds invalid opens its folded section first, so that it can be shown; a folded section
// whose fields were changed says so in its summary line instead of the summary of the saved values.
(() => {
    const toc = document.querySelector('[data-toc]');
    if (!toc) {
        return;
    }
    const links = [...toc.querySelectorAll('a[href^="#"]')];
    const sections = links.map((link) => document.getElementById(link.getAttribute('href').slice(1))).filter(Boolean);
    const mark = (id) => {
        for (const link of links) {
            link.setAttribute('aria-current', link.getAttribute('href') === '#' + id ? 'true' : 'false');
        }
    };
    // The current section is the last one whose top has passed a line a little below the top bar. After a jump from
    // the table of contents the chosen one stays marked until the reader scrolls by hand: a short section at the end
    // of the page may never reach the line.
    let pending = false;
    let chosen = null;
    const update = () => {
        pending = false;
        if (chosen) {
            mark(chosen);
            return;
        }
        const line = 120;
        let current = sections[0];
        for (const section of sections) {
            if (section.getBoundingClientRect().top <= line) {
                current = section;
            }
        }
        if (current) {
            mark(current.id);
        }
    };
    for (const type of ['wheel', 'touchstart', 'keydown']) {
        window.addEventListener(type, () => {
            chosen = null;
        }, { passive: true });
    }
    const schedule = () => {
        if (!pending) {
            pending = true;
            window.requestAnimationFrame(update);
        }
    };
    window.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule, { passive: true });
    update();
    toc.addEventListener('click', (event) => {
        const link = event.target.closest('a[href^="#"]');
        const target = link && document.getElementById(link.getAttribute('href').slice(1));
        if (!target) {
            return;
        }
        event.preventDefault();
        chosen = target.id;
        mark(chosen);
        if (target instanceof HTMLDetailsElement && !target.open) {
            target.classList.add('at-once');
            target.open = true;
            window.setTimeout(() => target.classList.remove('at-once'), 50);
        }
        target.scrollIntoView({ behavior: still() ? 'auto' : 'smooth', block: 'start' });
        history.replaceState(null, '', link.getAttribute('href'));
        const focusable = target.querySelector('summary, h2');
        if (focusable) {
            focusable.setAttribute('tabindex', focusable.tagName === 'SUMMARY' ? '0' : '-1');
            focusable.focus({ preventScroll: true });
        }
    });
    const target = location.hash && document.getElementById(location.hash.slice(1));
    if (target instanceof HTMLDetailsElement && target.classList.contains('fold')) {
        target.open = true;
    }
    // "invalid" does not bubble: listen while it goes down.
    document.addEventListener('invalid', (event) => {
        const fold = event.target.closest && event.target.closest('details.fold');
        if (fold) {
            fold.open = true;
        }
    }, true);
    const changed = (event) => {
        const fold = event.target.closest && event.target.closest('details.fold');
        const summary = fold && fold.querySelector('[data-fold-summary]');
        if (summary && summary.textContent !== summary.getAttribute('data-fold-summary')) {
            summary.textContent = summary.getAttribute('data-fold-summary');
        }
    };
    document.addEventListener('input', changed);
    document.addEventListener('change', changed);
})();

// Lists with quick choices and a search ([data-list-filter="id of the list"], the machine list): rows carry
// data-flags and data-search; a group (tbody[data-group]) without a shown row hides with its heading. The list and
// the choices are live parts (they come again from the server every few seconds): the choice made and the search typed
// are applied to them again each time.
(() => {
    for (const tools of document.querySelectorAll('[data-list-filter]')) {
        const id = tools.getAttribute('data-list-filter');
        const search = tools.querySelector('[data-list-search]');
        const pressed = tools.querySelector('[data-filter][aria-pressed="true"]');
        let filter = pressed ? pressed.getAttribute('data-filter') || '' : '';
        const apply = () => {
            for (const chip of tools.querySelectorAll('[data-filter]')) {
                chip.setAttribute('aria-pressed', String((chip.getAttribute('data-filter') || '') === filter));
            }
            const list = document.getElementById(id);
            if (!list) {
                return;
            }
            const empty = list.querySelector('[data-list-empty]');
            const table = list.querySelector('table');
            const words = (search ? search.value : '').trim().toLowerCase().split(/\s+/).filter(Boolean);
            let shown = 0;
            for (const body of list.querySelectorAll('tbody[data-group]')) {
                let inBody = 0;
                for (const row of body.querySelectorAll('tr[data-search]')) {
                    const flags = (row.getAttribute('data-flags') || '').split(' ');
                    const text = row.getAttribute('data-search') || '';
                    const ok = (filter === '' || flags.includes(filter)) && words.every((word) => text.includes(word));
                    row.hidden = !ok;
                    inBody += ok ? 1 : 0;
                }
                body.hidden = inBody === 0;
                shown += inBody;
            }
            if (empty) {
                empty.hidden = shown > 0;
            }
            if (table) {
                table.hidden = shown === 0;
            }
        };
        tools.addEventListener('click', (event) => {
            const chip = event.target.closest('[data-filter]');
            if (chip && !chip.disabled) {
                filter = chip.getAttribute('data-filter') || '';
                apply();
            }
        });
        if (search) {
            search.addEventListener('input', apply);
            search.addEventListener('keydown', (event) => {
                if (event.key === 'Escape' && search.value !== '') {
                    search.value = '';
                    apply();
                }
            });
        }
        // The browser may bring back the search text when the page is opened again with "Back".
        apply();
        window.addEventListener('pageshow', apply);
        document.addEventListener('custos:parts', apply);
    }
})();

// Ripple (Material 3): pressing a button, a menu item, a chip, a tab or a section of the menu spreads a circle from
// the pointer that fades when the press ends. In the narrow rail and the phone's bottom bar it fills the pill around
// the icon. Nothing of it with less motion asked for.
(() => {
    const hosts = 'button, a.button-link, details.dropdown > summary, .icon-button, .menu-item, .nav-item, .bar-item, .tabs a, .editor-toc a, a.tile, .sr-item';
    document.addEventListener('pointerdown', (event) => {
        if (still() || event.button !== 0) {
            return;
        }
        const el = event.target.closest(hosts);
        if (!el || el.disabled || el.classList.contains('link') || el.closest('.month-days')) {
            return;
        }
        const pill = el.querySelector(':scope > .nav-icon');
        const host = pill && getComputedStyle(pill).display !== 'contents' ? pill : el;
        const box = host.getBoundingClientRect();
        if (box.width === 0) {
            return;
        }
        const x = event.clientX - box.left;
        const y = event.clientY - box.top;
        const size = 2 * Math.hypot(Math.max(x, box.width - x), Math.max(y, box.height - y));
        const wave = document.createElement('span');
        wave.className = 'ripple';
        const dot = document.createElement('i');
        dot.style.width = dot.style.height = size + 'px';
        dot.style.left = (x - size / 2) + 'px';
        dot.style.top = (y - size / 2) + 'px';
        wave.append(dot);
        host.append(wave);
        const born = performance.now();
        const end = () => {
            document.removeEventListener('pointerup', end);
            document.removeEventListener('pointercancel', end);
            // The circle grows to its full size before it fades, however short the press.
            setTimeout(() => {
                wave.classList.add('fading');
                setTimeout(() => wave.remove(), 350);
            }, Math.max(0, 220 - (performance.now() - born)));
        };
        document.addEventListener('pointerup', end);
        document.addEventListener('pointercancel', end);
    });
})();

// Fields (Material 3 outlined): the text of a short label moves into a notch of the field's frame. It works on labels
// written as <label>Text <input ...></label> (also select, textarea and a field with its unit; the text may be in
// spans, as when it follows the kind of a destination); a label whose text is longer than its field, or with
// something else before the field, stays above it as before. A field that is hidden
// (a folded section, a part shown for one kind of destination, a closed dialog) is measured again once it shows.
(() => {
    const fieldOf = (label) => {
        if (label.matches('.check, .choice, .day, .search-field') || label.closest('.month-days')) {
            return null;
        }
        let text = '';
        for (const node of label.childNodes) {
            if (node.nodeType === Node.TEXT_NODE) {
                text += node.textContent;
                continue;
            }
            if (node.nodeType !== Node.ELEMENT_NODE) {
                continue;
            }
            if (node.matches('span:not(.unit-input):not(.note), b')) {
                text += node.textContent;
                continue;
            }
            const field = node.matches('select, textarea, .unit-input')
                || (node.matches('input') && !/^(checkbox|radio|hidden|file|range|color|submit|button)$/.test(node.type));
            return field && text.trim() !== '' ? node : null;
        }
        return null;
    };
    const prepare = (label) => {
        label.setAttribute('data-fl', '');
        const field = fieldOf(label);
        if (!field) {
            return;
        }
        const span = document.createElement('span');
        span.className = 'fl';
        for (const node of [...label.childNodes]) {
            if (node === field) {
                break;
            }
            span.append(node);
        }
        label.insertBefore(span, field);
    };
    const measure = (label) => {
        const span = label.querySelector(':scope > .fl');
        const field = span && span.nextElementSibling;
        if (!field) {
            return;
        }
        const width = field.offsetWidth;
        if (width === 0) {
            return; // hidden now: measured again when it shows
        }
        label.classList.add('fl-on');
        span.style.maxWidth = Math.max(0, width - 22) + 'px';
        const fits = span.scrollWidth <= span.clientWidth + 1;
        label.classList.toggle('fl-on', fits);
        if (!fits) {
            span.style.maxWidth = '';
        }
    };
    let queued = false;
    const run = () => {
        queued = false;
        for (const label of document.querySelectorAll('label:not([data-fl])')) {
            prepare(label);
        }
        for (const label of document.querySelectorAll('label[data-fl]')) {
            measure(label);
        }
        // Fields side by side keep their frames on one line: when one label of a row has to stay above its field,
        // the others of the row stand above theirs too.
        for (const row of document.querySelectorAll('.field-grid, .row-fields, form.filters')) {
            const labels = [...row.querySelectorAll('label[data-fl]')].filter((label) => label.querySelector(':scope > .fl'));
            if (labels.some((label) => !label.classList.contains('fl-on') && label.offsetWidth > 0)) {
                labels.forEach((label) => label.classList.remove('fl-on'));
            }
        }
    };
    const later = () => {
        if (!queued) {
            queued = true;
            window.requestAnimationFrame(run);
        }
    };
    run();
    for (const name of ['change', 'click', 'custos:parts']) {
        document.addEventListener(name, later);
    }
    document.addEventListener('toggle', later, true);
    let resizing = 0;
    window.addEventListener('resize', () => {
        clearTimeout(resizing);
        resizing = setTimeout(later, 150);
    });
    if (document.fonts && document.fonts.ready) {
        document.fonts.ready.then(later);
    }
})();

// Search of machines and tasks (top bar): matches show under the field as one types; the arrows choose one, Enter
// opens it (or, with none chosen, the page with all matches), Esc closes the list. "/" anywhere outside a field puts
// the cursor in the search. On a phone the field hides behind a button of the top bar.
(() => {
    const form = document.querySelector('form.search[data-search]');
    if (!form) {
        return;
    }
    const input = form.querySelector('input[name="q"]');
    const box = form.querySelector('.search-results');
    const source = form.getAttribute('data-search');
    let timer = 0;
    let asked = 0;
    let items = [];
    let active = -1;
    let lastQuery = null;

    const show = (on) => {
        box.hidden = !on;
        input.setAttribute('aria-expanded', on ? 'true' : 'false');
    };
    const choose = (i) => {
        active = i;
        items.forEach((item, k) => item.setAttribute('aria-selected', k === i ? 'true' : 'false'));
        if (items[i]) {
            input.setAttribute('aria-activedescendant', items[i].id);
            items[i].scrollIntoView({ block: 'nearest' });
        } else {
            input.removeAttribute('aria-activedescendant');
        }
    };
    const ask = async () => {
        const q = input.value.trim();
        if (q === lastQuery) {
            show(q !== '' && box.innerHTML.trim() !== '');
            return;
        }
        lastQuery = q;
        if (q === '') {
            box.innerHTML = '';
            items = [];
            show(false);
            return;
        }
        const mine = ++asked;
        let html;
        try {
            const response = await fetch(source + (source.includes('?') ? '&' : '?') + 'q=' + encodeURIComponent(q), {
                credentials: 'same-origin',
                cache: 'no-store',
            });
            if (response.status === 401) {
                window.location.reload();
                return;
            }
            if (!response.ok) {
                lastQuery = null;
                return;
            }
            html = await response.text();
        } catch {
            lastQuery = null;
            return;
        }
        if (mine !== asked) {
            return; // a newer question is on its way
        }
        box.innerHTML = html;
        items = [...box.querySelectorAll('.sr-item')];
        choose(-1);
        show(document.activeElement === input && html.trim() !== '');
    };
    const close = () => {
        show(false);
        choose(-1);
    };
    const open = () => {
        document.body.classList.add('searching');
        input.focus();
        input.select();
    };

    input.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(ask, 140);
    });
    input.addEventListener('focus', () => {
        if (input.value.trim() !== '') {
            ask();
        }
    });
    input.addEventListener('keydown', (event) => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            if (box.hidden || items.length === 0) {
                return;
            }
            event.preventDefault();
            // Down from the last match and up from the first one come back to the field.
            let next = active + (event.key === 'ArrowDown' ? 1 : -1);
            if (next >= items.length) {
                next = -1;
            } else if (next < -1) {
                next = items.length - 1;
            }
            choose(next);
        } else if (event.key === 'Enter' && !box.hidden && items[active]) {
            event.preventDefault();
            window.location.href = items[active].href;
        } else if (event.key === 'Escape') {
            if (!box.hidden) {
                close();
            } else {
                input.blur();
            }
        }
    });
    // A click on a match must not take the focus from the field first (the list would close under the pointer).
    box.addEventListener('mousedown', (event) => event.preventDefault());
    form.addEventListener('submit', (event) => {
        if (input.value.trim() === '') {
            event.preventDefault();
        }
    });
    form.addEventListener('focusout', () => {
        setTimeout(() => {
            if (!form.contains(document.activeElement)) {
                close();
                document.body.classList.remove('searching');
            }
        }, 0);
    });
    document.addEventListener('click', (event) => {
        if (event.target.closest('[data-search-open]')) {
            open();
        }
    });
    document.addEventListener('keydown', (event) => {
        if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || event.defaultPrevented) {
            return;
        }
        const target = event.target;
        if (target instanceof Element && target.closest('input, textarea, select, [contenteditable], dialog[open]')) {
            return;
        }
        event.preventDefault();
        open();
    });
})();
