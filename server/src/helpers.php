<?php

// Template helpers. They work with the view being rendered (Custos\Http\Web\View::$current).

declare(strict_types=1);

use Custos\Http\Web\View;

function e(mixed $value): string
{
    return htmlspecialchars((string) $value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function t(string $key, array $params = []): string
{
    return View::$current === null ? $key : View::$current->app->lang()->get($key, $params);
}

function url(string $path, array $query = []): string
{
    return View::$current->url($path, $query);
}

function asset(string $file): string
{
    return View::$current->asset($file);
}

function csrf_field(): string
{
    return '<input type="hidden" name="_csrf" value="' . e(View::$current->session->csrf()) . '">';
}

/** A UTC time from the database shown in the site time zone. */
function fmt_time(?string $utc, bool $seconds = false): string
{
    if ($utc === null || $utc === '') {
        return '—';
    }
    $time = new DateTimeImmutable($utc, new DateTimeZone('UTC'));
    $zone = new DateTimeZone(View::$current->app->config->timezone());
    return $time->setTimezone($zone)->format(t($seconds ? 'format.datetime_seconds' : 'format.datetime'));
}

/** A day "Y-m-d" as the language writes a short date: "12.11", "11-12". */
function fmt_day(string $day): string
{
    return (new DateTimeImmutable($day))->format(t('format.day'));
}

function fmt_ago(?string $utc): string
{
    if ($utc === null || $utc === '') {
        return t('time.never');
    }
    $seconds = max(0, time() - (new DateTimeImmutable($utc, new DateTimeZone('UTC')))->getTimestamp());
    return match (true) {
        $seconds < 60 => t('time.just_now'),
        $seconds < 3600 => t('time.minutes_ago', ['n' => intdiv($seconds, 60)]),
        $seconds < 86400 => t('time.hours_ago', ['n' => intdiv($seconds, 3600)]),
        default => t('time.days_ago', ['n' => intdiv($seconds, 86400)]),
    };
}

function fmt_bytes(int|string|null $bytes): string
{
    if ($bytes === null || $bytes === '') {
        return '—';
    }
    $value = (float) $bytes;
    $units = ['unit.b', 'unit.kb', 'unit.mb', 'unit.gb', 'unit.tb'];
    $i = 0;
    while ($value >= 1024 && $i < count($units) - 1) {
        $value /= 1024;
        $i++;
    }
    $number = $i === 0 ? (string) (int) $value : number_format($value, $value < 10 ? 1 : 0, t('format.decimal'), '');
    return $number . ' ' . t($units[$i]);
}

function fmt_number(int|string|null $value): string
{
    return $value === null || $value === '' ? '—' : number_format((float) $value, 0, t('format.decimal'), t('format.thousands'));
}

function fmt_duration(int|string|null $seconds): string
{
    if ($seconds === null || $seconds === '') {
        return '—';
    }
    $seconds = (int) $seconds;
    if ($seconds < 60) {
        return t('duration.seconds', ['s' => $seconds]);
    }
    if ($seconds < 3600) {
        return t('duration.minutes', ['m' => intdiv($seconds, 60), 's' => $seconds % 60]);
    }
    return t('duration.hours', ['h' => intdiv($seconds, 3600), 'm' => intdiv($seconds % 3600, 60)]);
}

/** Colour of a status (6.6): red only for error and missed, yellow for warning and anomaly, blue while running. */
function status_class(string $status): string
{
    return match ($status) {
        'success', 'online', 'ok', 'done', 'sent' => 'ok',
        'warning', 'anomaly', 'space' => 'warn',
        'error', 'failed', 'missed' => 'bad',
        'running', 'paused', 'pending', 'queued', 'delivered' => 'info',
        default => 'muted',
    };
}

/** A coloured status label. Colours follow section 6.6. */
function badge(string $status, ?string $label = null): string
{
    $class = status_class($status);
    $lang = View::$current?->app->lang();
    $label ??= $lang !== null && $lang->has('status.' . $status) ? $lang->get('status.' . $status) : $status;
    // A run in progress gets a pulsing dot (app.css), so a page that shows one says it is alive.
    $live = $status === 'running' ? ' badge-live' : '';
    return '<span class="badge badge-' . $class . $live . '">' . e($label) . '</span>';
}

/** Short schedule text: "ежедневно 00:00", "еженедельно: вс 01:00", "каждые 15 мин". */
function schedule_label(mixed $schedule): string
{
    if (!is_array($schedule)) {
        return '—';
    }
    $times = implode(', ', array_map('strval', (array) ($schedule['times'] ?? [])));
    $weekdays = array_values(array_filter((array) ($schedule['days_of_week'] ?? []), 'is_int'));
    sort($weekdays);
    $days = implode(', ', array_map(fn(int $d) => t('weekday.' . $d), $weekdays));
    switch ($schedule['type'] ?? '') {
        case 'daily':
            $text = t('schedule.daily', ['times' => $times]);
            return $weekdays === [] || $weekdays === [1, 2, 3, 4, 5, 6, 7] ? $text : $text . ' (' . $days . ')';
        case 'weekly':
            return t('schedule.weekly', ['days' => $days, 'times' => $times]);
        case 'monthly':
            $monthDays = array_map(
                fn($d) => $d === -1 ? t('schedule.last_day') : (string) $d,
                (array) ($schedule['days_of_month'] ?? [])
            );
            return t('schedule.monthly', ['days' => implode(', ', $monthDays), 'times' => $times]);
        case 'interval':
            return t('schedule.interval', ['minutes' => (int) ($schedule['interval_minutes'] ?? 0)]);
        case 'manual':
            return t('schedule.manual');
        default:
            return (string) ($schedule['type'] ?? '—');
    }
}

/** Page links for lists: « 1 2 3 ». */
function pager(int $page, int $pages, string $path, array $query): string
{
    if ($pages <= 1) {
        return '';
    }
    $html = '<nav class="pager">';
    for ($i = max(1, $page - 5); $i <= min($pages, $page + 5); $i++) {
        $html .= $i === $page
            ? '<span class="current">' . $i . '</span>'
            : '<a href="' . e(url($path, ['page' => $i] + $query)) . '">' . $i . '</a>';
    }
    return $html . '</nav>';
}

/** A UTC time from the database in the agent's time zone: schedule slots read the same as the schedule. */
function fmt_local(?string $utc, string $timeZone): string
{
    return $utc === null || $utc === '' ? '—' : \Custos\Domain\Notifier::localTime($utc, $timeZone);
}

/** A unix time in the site time zone. */
function fmt_unix(?int $time): string
{
    return $time === null ? '—' : fmt_time(gmdate('Y-m-d H:i:s', $time));
}

/** What a problem is about, in one line for the site. */
function problem_text(array $p): string
{
    if ($p['kind'] === 'space') {
        $days = (int) ($p['days_left'] ?? 0);
        return t($days === 0 ? 'problem.space.full' : 'problem.space', ['path' => (string) $p['message'], 'n' => $days]);
    }
    if ($p['kind'] === 'error') {
        $text = $p['message'] === null ? t('problem.error') : t('problem.error.message', ['message' => $p['message']]);
    } else {
        $time = fmt_local($p['first_slot_at'], (string) ($p['time_zone'] ?? 'UTC'));
        $text = t($p['slot_kind'] === 'interval' ? 'problem.missed.interval' : 'problem.missed', ['time' => $time]);
    }
    if ((int) $p['occurrences'] > 1) {
        $text .= ' ' . t($p['kind'] === 'error' ? 'problem.more.error' : 'problem.more.missed', ['n' => (int) $p['occurrences']]);
    }
    return $text;
}

/** What changed in an anomalous run: "files −60 %, size −58 %". */
function anomaly_text(?string $json): string
{
    $data = json_decode((string) $json, true);
    if (!is_array($data)) {
        return '';
    }
    $parts = [];
    foreach (['source_files' => 'anomaly.files', 'source_bytes' => 'anomaly.bytes'] as $field => $key) {
        if (isset($data[$field]['change_pct'])) {
            $pct = (int) $data[$field]['change_pct'];
            $parts[] = t($key, ['change' => ($pct > 0 ? '+' : '−') . abs($pct) . ' %']);
        }
    }
    return implode(', ', $parts);
}

/** A destination of a task in a few words: a path, "USB «LABEL» \\path", "sftp://host/path", "s3://bucket". */
function destination_label(array $d): string
{
    $kind = (string) ($d['kind'] ?? '');
    $path = (string) ($d['path'] ?? '');
    return match ($kind) {
        'local', 'unc' => $path,
        'usb' => trim('USB «' . ($d['usb_label'] ?? '') . '» ' . $path),
        's3' => 's3://' . ($d['bucket'] ?? '') . ($path !== '' ? '/' . ltrim($path, '/') : ''),
        default => $kind . '://' . ($d['host'] ?? '') . ($path !== '' ? '/' . ltrim($path, '/') : ''),
    };
}

/**
 * A task in human language (7.6), so that a wrong setting is easy to spot:
 * "Инкрементальная копия D:\Data → \\fileserver\docs · ежедневно 00:00 · без сжатия · хранить 12 полных копий · VSS".
 */
function task_summary(array $task): string
{
    $parts = [];
    $sources = array_map(fn($s) => (string) ($s['path'] ?? ''), array_filter((array) ($task['sources'] ?? []), 'is_array'));
    $destinations = array_map('destination_label', array_filter((array) ($task['destinations'] ?? []), 'is_array'));
    $type = (string) ($task['backup_type'] ?? '');
    $head = t('summary.type.' . $type);
    if ($type === 'mirror' && ($task['mirror_mode'] ?? null) !== null) {
        $head .= ' (' . t('summary.mirror.' . $task['mirror_mode']) . ')';
    }
    $databases = db_servers_label($task);
    if ($databases !== '') {
        $sources[] = t('summary.databases', ['servers' => $databases]);
    }
    $parts[] = trim($head . ' ' . implode(', ', $sources) . ($destinations === [] ? '' : ' → ' . implode(', ', $destinations)));
    $parts[] = schedule_label($task['schedule'] ?? null);

    $archive = (array) ($task['archive'] ?? []);
    $format = (string) ($archive['format'] ?? 'none');
    if ($format === 'none') {
        $parts[] = t('summary.no_compression');
    } else {
        $text = t('summary.archive', ['format' => $format, 'level' => (int) ($archive['level'] ?? 5)]);
        if (!empty($archive['encrypt'])) {
            $text .= ', ' . t(!empty($archive['encrypt_names']) ? 'summary.encrypted_names' : 'summary.encrypted');
        }
        if ((int) ($archive['split_mb'] ?? 0) > 0) {
            $text .= ', ' . t('summary.split', ['mb' => (int) $archive['split_mb']]);
        }
        $parts[] = $text;
    }

    $retention = (array) ($task['retention'] ?? []);
    $parts[] = match ($retention['mode'] ?? 'count') {
        'none' => t('summary.keep_all'),
        'age' => t('summary.keep_days', ['n' => (int) ($retention['max_age_days'] ?? 0)]),
        'gfs' => t('summary.keep_gfs', [
            'd' => (int) ($retention['gfs']['daily'] ?? 0), 'w' => (int) ($retention['gfs']['weekly'] ?? 0),
            'm' => (int) ($retention['gfs']['monthly'] ?? 0),
        ]),
        default => t('summary.keep_full', ['n' => (int) ($retention['keep_full'] ?? 0)]),
    };
    if (!empty($task['vss']['enabled'])) {
        $parts[] = 'VSS';
    }
    $pre = count((array) ($task['events']['pre'] ?? []));
    $post = count((array) ($task['events']['post'] ?? []));
    if ($pre + $post > 0) {
        $parts[] = t('summary.events', ['pre' => $pre, 'post' => $post]);
    }
    $after = (string) ($task['events']['after'] ?? 'none');
    if ($after !== 'none') {
        $parts[] = t('summary.after.' . $after);
    }
    if (isset($task['enabled']) && $task['enabled'] === false) {
        $parts[] = t('summary.disabled');
    }
    return implode(' · ', array_filter($parts, fn($p) => $p !== ''));
}

/** The space forecast of a destination (section 11): "≈ 40 дн. до заполнения", "место не убывает". */
function forecast_text(?array $f): string
{
    return match ($f['state'] ?? 'unknown') {
        'filling' => (int) $f['days_left'] === 0 ? t('forecast.full') : t('forecast.days', ['n' => (int) $f['days_left']]),
        'steady' => t('forecast.steady'),
        default => t('forecast.unknown'),
    };
}

/**
 * The id of one destination in the storage block of the machine page, so a link can bring it into view: the dashboard
 * tile "Least free space" ends its link with it.
 */
function storage_anchor(array $storage): string
{
    return 'storage-' . $storage['task_id'] . '-' . $storage['destination_id'];
}

/** Database servers of a task (A.9 db_servers, client stage 2b) in a few words: "MySQL 127.0.0.1 (все базы)". */
function db_servers_label(array $task): string
{
    $parts = [];
    foreach ((array) ($task['db_servers'] ?? []) as $server) {
        if (!is_array($server)) {
            continue;
        }
        $engine = (string) ($server['engine'] ?? '');
        $name = t('db.engine.' . $engine) === 'db.engine.' . $engine ? $engine : t('db.engine.' . $engine);
        $host = (string) ($server['host'] ?? '');
        $port = $server['port'] ?? null;
        $which = !empty($server['all_databases'])
            ? t('db.all')
            : implode(', ', array_map('strval', array_filter((array) ($server['include'] ?? []), 'is_scalar')));
        $parts[] = trim($name . ' ' . $host . ($port !== null ? ':' . (int) $port : '')) . ($which === '' ? '' : ' (' . $which . ')');
    }
    return implode(', ', $parts);
}

/** A notice of the bell (7.7) as text. */
function notice_text(array $n): string
{
    $params = json_decode((string) $n['params'], true) ?: [];
    if (isset($params['kind'])) {
        $params['kind'] = t('command.' . $params['kind']) === 'command.' . $params['kind'] ? $params['kind'] : t('command.' . $params['kind']);
    }
    if (isset($params['status'])) {
        $params['status'] = t('status.' . $params['status']);
    }
    $params['machine'] = ($n['hostname'] ?? null) === null ? '' : \Custos\Domain\Agents::name($n);
    return t('notice.' . $n['kind'], $params);
}

/** A message whose parameters may be messages too: ['key' => …, 'params' => […]] (errors of the task editor). */
function t_msg(array $message): string
{
    $params = [];
    foreach ((array) ($message['params'] ?? []) as $name => $value) {
        $params[$name] = is_array($value) && isset($value['key']) ? t_msg($value) : $value;
    }
    return t((string) $message['key'], $params);
}

/** An account in a few words: "OFFICE\backup1 (Windows)", "пароль архива". */
function account_label(array $c): string
{
    $kind = (string) ($c['kind'] ?? '');
    if ($kind === 'archive_password') {
        return t('credential.kind.archive_password');
    }
    $name = \Custos\Domain\Templates::accountName($c);
    return ($name === '' ? '—' : $name) . ' (' . t('credential.kind.' . $kind) . ')';
}

/** "checked" for a checkbox or radio button. */
function checked(bool $on): string
{
    return $on ? ' checked' : '';
}

/** "selected" for an option. */
function selected(mixed $value, mixed $current): string
{
    return (string) $value === (string) $current ? ' selected' : '';
}

/** One of the site's icons (templates/_sprite.php). It is decoration: the word next to it says what it means. */
function icon(string $name): string
{
    return '<svg class="icon" aria-hidden="true" focusable="false"><use href="#i-' . e($name) . '"/></svg>';
}
