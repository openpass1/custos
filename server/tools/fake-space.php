<?php

// Checking the storage warning (section 11) on a test site: makes the storages of one machine look as if they fill up.
// Run without arguments for help. Only for test sites: it writes made-up free space into the history.

declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    exit("Run this script from the command line.\n");
}
require dirname(__DIR__) . '/vendor/autoload.php';

use Custos\Cli;
use Custos\Infra\Time;

const HELP = <<<'TEXT'
Usage: php tools/fake-space.php --host=FAKE-PC-01 [--days=7]
       php tools/fake-space.php --undo

Makes the storages of one machine look as if they fill up, to check the storage warning without waiting for weeks.
Adds hourly free-space points for the last 5 days that shrink so that, at this rate, the space runs out in --days days
(default 7), hides the real points of the last 30 days from the forecast, then recalculates the forecasts and the
"space" problems at once. The message about a new problem goes out with the next cron pass (within a minute).

  --host=NAME   machine name as on the site (host name or display name)
  --days=N      days until the storage is full, 1-365 (default 7)
  --undo        removes the points this script added, brings back the hidden ones and recalculates;
                the problem closes if the real forecast is calm

Only for test sites: what it changed is listed in var/fake-space.json so that --undo can put it back.

TEXT;

$app = Cli::boot();
$options = Cli::options($argv);
$db = $app->db();
$storage = $app->storage();
$listFile = $app->config->varDir() . '/fake-space.json';
// {added: [storage_space ids], hidden: {id: free_bytes}}: the rows this script added and the real ones it hid.
$changes = is_file($listFile) ? json_decode((string) file_get_contents($listFile), true) : null;
$changes = is_array($changes) ? $changes + ['added' => [], 'hidden' => []] : ['added' => [], 'hidden' => []];

$recalculate = function () use ($storage): void {
    $now = time();
    $storage->recalculate($now);
    $stats = $storage->checkSpace($now);
    Cli::ok("Forecasts recalculated; space problems opened: {$stats['opened']}, closed: {$stats['closed']}");
};

if (isset($options['undo'])) {
    $ids = array_values(array_filter(array_map('intval', $changes['added'])));
    foreach (array_chunk($ids, 500) as $chunk) {
        $db->exec('DELETE FROM storage_space WHERE id IN (' . implode(',', array_fill(0, count($chunk), '?')) . ')', $chunk);
    }
    foreach ($changes['hidden'] as $id => $free) {
        $db->exec('UPDATE storage_space SET free_bytes = ? WHERE id = ? AND free_bytes IS NULL', [$free, (int) $id]);
    }
    @unlink($listFile);
    Cli::ok('Removed points: ' . count($ids) . ', brought back: ' . count($changes['hidden']));
    $recalculate();
    exit(0);
}

$host = (string) ($options['host'] ?? '');
if ($host === '' || $host === '1') {
    fwrite(STDOUT, HELP);
    exit($host === '' && $options === [] ? 0 : 1);
}
$days = (int) ($options['days'] ?? 7);
if ($days < 1 || $days > 365) {
    Cli::fail('--days must be between 1 and 365');
}
$agent = $db->one(
    "SELECT id, hostname, display_name FROM agents WHERE (hostname = ? OR display_name = ?) AND status = 'approved' ORDER BY hostname LIMIT 1",
    [$host, $host]
) ?? Cli::fail("No approved machine named $host");

// The latest report of every destination of the machine: the points are built back from it.
$latest = $db->all(
    'SELECT s.* FROM storage_space s
     JOIN (SELECT task_id, destination_id, MAX(id) AS id FROM storage_space WHERE agent_id = ? GROUP BY task_id, destination_id) last
       ON last.id = s.id
     WHERE s.free_bytes IS NOT NULL',
    [$agent['id']]
);
if ($latest === []) {
    Cli::fail('The machine has not reported free space yet: run the fake agent once (--once) and try again.');
}
$now = time();
$insert = 'INSERT INTO storage_space (agent_id, task_id, destination_id, kind, path, total_bytes, free_bytes, recorded_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?)';
$windowStart = Time::db($now - \Custos\Domain\Storage::WINDOW_DAYS * 86400);
foreach ($latest as $row) {
    // The real points of the window would flatten the made-up trend: their free space is set aside until --undo.
    foreach ($db->all(
        'SELECT id, free_bytes FROM storage_space
         WHERE agent_id = ? AND task_id = ? AND destination_id = ? AND recorded_at >= ? AND free_bytes IS NOT NULL',
        [$row['agent_id'], $row['task_id'], $row['destination_id'], $windowStart]
    ) as $point) {
        if (!in_array((int) $point['id'], array_map('intval', $changes['added']), true)) {
            $changes['hidden'][$point['id']] = $point['free_bytes'];
        }
        $db->exec('UPDATE storage_space SET free_bytes = NULL WHERE id = ?', [$point['id']]);
    }
    $free = (int) $row['free_bytes'];
    $perDay = $free / $days;
    $total = $row['total_bytes'] === null ? PHP_INT_MAX : (int) $row['total_bytes'];
    for ($hours = 5 * 24; $hours >= 1; $hours--) {
        $value = min($total, (int) round($free + $perDay * $hours / 24));
        $db->exec($insert, [$row['agent_id'], $row['task_id'], $row['destination_id'], $row['kind'], $row['path'],
                            $row['total_bytes'], $value, Time::db($now - $hours * 3600)]);
        $changes['added'][] = $db->lastInsertId();
    }
    // A fresh copy of the latest report, so that it stays the newest row the pages show.
    $db->exec($insert, [$row['agent_id'], $row['task_id'], $row['destination_id'], $row['kind'], $row['path'],
                        $row['total_bytes'], $free, Time::db($now)]);
    $changes['added'][] = $db->lastInsertId();
}
file_put_contents($listFile, json_encode($changes));
Cli::ok('Added free-space history for ' . count($latest) . ' storage(s) of ' . ($agent['display_name'] ?: $agent['hostname']));
$recalculate();

$limit = $app->settings()->int('space_warn_days');
foreach ($db->all(
    "SELECT f.state, f.days_left, t.name AS task_name,
            (SELECT s.path FROM storage_space s WHERE s.agent_id = f.agent_id AND s.task_id = f.task_id AND s.destination_id = f.destination_id
             ORDER BY s.id DESC LIMIT 1) AS path
     FROM storage_forecasts f LEFT JOIN tasks t ON t.agent_id = f.agent_id AND t.id = f.task_id
     WHERE f.agent_id = ? ORDER BY t.name",
    [$agent['id']]
) as $f) {
    $forecast = $f['state'] === 'filling' ? "full in {$f['days_left']} days" : $f['state'];
    Cli::info("{$f['task_name']}: {$f['path']}: $forecast");
}
Cli::info($limit > 0 ? "Warning limit (Settings → Thresholds): $limit days" : 'The warning is off (Settings → Thresholds: 0 days)');
