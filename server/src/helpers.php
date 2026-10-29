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
    return $time->setTimezone($zone)->format($seconds ? 'd.m.Y H:i:s' : 'd.m.Y H:i');
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
    $number = $i === 0 ? (string) (int) $value : number_format($value, $value < 10 ? 1 : 0, ',', '');
    return $number . ' ' . t($units[$i]);
}

function fmt_number(int|string|null $value): string
{
    return $value === null || $value === '' ? '—' : number_format((float) $value, 0, ',', ' ');
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

/** A coloured status label. Colours follow section 6.6. */
function badge(string $status, ?string $label = null): string
{
    $class = match ($status) {
        'success', 'online', 'ok', 'done' => 'ok',
        'warning' => 'warn',
        'error', 'failed' => 'bad',
        'running', 'paused', 'pending', 'queued', 'delivered' => 'info',
        default => 'muted',
    };
    $lang = View::$current?->app->lang();
    $label ??= $lang !== null && $lang->has('status.' . $status) ? $lang->get('status.' . $status) : $status;
    return '<span class="badge badge-' . $class . '">' . e($label) . '</span>';
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
