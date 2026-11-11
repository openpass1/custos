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
                el.hidden = !holds(scope, el.getAttribute('data-show-if'));
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
    if (document.querySelector('[data-show-if], [data-editor]')) {
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
