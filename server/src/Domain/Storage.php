<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Storages from the inventory (A.8): free space with its forecast (section 11) and the sets that lie in each
 * destination (7.3).
 *
 * Forecast: a least-squares line through the free space of the last 30 days; while it goes down, the days until it
 * reaches zero. Cron recalculates it once an hour, and with it the "space" problems: a task gets one when a destination
 * of it will be full in space_warn_days days or less.
 */
final class Storage
{
    public const WINDOW_DAYS = 30;
    public const EVERY_S = 3600;
    /** A trend needs this many points over at least this long, otherwise the forecast is "unknown". */
    public const MIN_POINTS = 3;
    public const MIN_SPAN_S = 86400;
    /** Shrinking slower than this many bytes a day counts as steady (rounding noise of a few kilobytes). */
    private const NOISE_BYTES_DAY = 1024 * 1024;
    /** Forecasts further away than this are not worth a number. */
    public const MAX_DAYS = 3650;
    /**
     * A space problem closes when the forecast goes beyond this many times the warning limit, not right above it:
     * free space jumps up and down as old sets are deleted, and the problem should not open and close every hour.
     */
    public const CLOSE_FACTOR = 2;

    public function __construct(private readonly Db $db, private readonly Settings $settings)
    {
    }

    /**
     * Cron step: recalculates every forecast and the space problems once an hour.
     *
     * @return ?array{destinations: int, opened: int, closed: int} null if not due
     */
    public function pass(int $now): ?array
    {
        $last = $this->settings->get('forecast_last_run');
        if ($last !== '' && Time::unix($last) > $now - self::EVERY_S) {
            return null;
        }
        $this->settings->set('forecast_last_run', Time::db($now));
        return ['destinations' => $this->recalculate($now)] + $this->checkSpace($now);
    }

    /** Whether a forecast is close enough to the end to show it in red: within the warning limit. */
    public function soon(?string $state, int|string|null $daysLeft): bool
    {
        $limit = $this->settings->int('space_warn_days');
        return $limit > 0 && $state === 'filling' && $daysLeft !== null && (int) $daysLeft <= $limit;
    }

    /**
     * Opens, updates and closes the "space" problems from the current forecasts (one per task, for its destination
     * that fills up first). Only enabled tasks of approved machines; a disabled task or machine has its problem closed
     * by Monitor::sweep. A limit of 0 closes every open one.
     *
     * @return array{opened: int, closed: int}
     */
    public function checkSpace(int $now): array
    {
        $limit = $this->settings->int('space_warn_days');
        $worst = [];
        if ($limit > 0) {
            foreach ($this->db->all(
                "SELECT f.agent_id, f.task_id, f.destination_id, f.days_left, t.data AS task_data,
                        (SELECT s.path FROM storage_space s
                         WHERE s.agent_id = f.agent_id AND s.task_id = f.task_id AND s.destination_id = f.destination_id
                         ORDER BY s.id DESC LIMIT 1) AS path
                 FROM storage_forecasts f
                 JOIN agents a ON a.id = f.agent_id AND a.status = 'approved'
                 JOIN tasks t ON t.agent_id = f.agent_id AND t.id = f.task_id AND t.deleted = 0 AND t.enabled = 1
                 WHERE f.state = 'filling' AND f.days_left <= ?",
                [$limit * self::CLOSE_FACTOR]
            ) as $row) {
                if (!self::taskHas($row['task_data'], $row['destination_id'])) {
                    continue;
                }
                $key = $row['agent_id'] . '/' . $row['task_id'];
                if (!isset($worst[$key]) || (int) $row['days_left'] < (int) $worst[$key]['days_left']) {
                    $worst[$key] = $row;
                }
            }
        }
        $nowDb = Time::db($now);
        $stats = ['opened' => 0, 'closed' => 0];
        foreach ($this->db->all("SELECT id, agent_id, task_id FROM problems WHERE kind = 'space' AND closed_at IS NULL") as $open) {
            $key = $open['agent_id'] . '/' . $open['task_id'];
            if (isset($worst[$key])) {
                $this->db->exec('UPDATE problems SET message = ?, days_left = ? WHERE id = ?',
                    [(string) $worst[$key]['path'], (int) $worst[$key]['days_left'], $open['id']]);
                unset($worst[$key]);
                continue;
            }
            $stats['closed'] += $this->db->exec(
                "UPDATE problems SET closed_at = ?, closed_by = 'space_ok' WHERE id = ? AND closed_at IS NULL",
                [$nowDb, $open['id']]
            );
        }
        foreach ($worst as $row) {
            if ((int) $row['days_left'] > $limit) {
                continue;
            }
            // The unique key on open problems keeps this to one per task even if two passes overlap.
            $stats['opened'] += $this->db->exec(
                "INSERT IGNORE INTO problems (agent_id, task_id, kind, opened_at, last_at, message, days_left)
                 VALUES (?, ?, 'space', ?, ?, ?, ?)",
                [$row['agent_id'], $row['task_id'], $nowDb, $nowDb, (string) $row['path'], (int) $row['days_left']]
            );
        }
        return $stats;
    }

    public function recalculate(int $now): int
    {
        $since = Time::db($now - self::WINDOW_DAYS * 86400);
        $series = [];
        foreach ($this->db->all(
            'SELECT agent_id, task_id, destination_id, free_bytes, recorded_at FROM storage_space
             WHERE recorded_at >= ? AND free_bytes IS NOT NULL ORDER BY recorded_at, id',
            [$since]
        ) as $row) {
            $series[$row['agent_id'] . '/' . $row['task_id'] . '/' . $row['destination_id']][] = [Time::unix($row['recorded_at']), (float) $row['free_bytes']];
        }
        $computed = Time::db($now);
        $this->db->transaction(function () use ($series, $computed) {
            $this->db->exec('DELETE FROM storage_forecasts');
            foreach ($series as $key => $points) {
                [$agentId, $taskId, $destinationId] = explode('/', $key);
                $f = self::forecast($points);
                $this->db->exec(
                    'INSERT INTO storage_forecasts (agent_id, task_id, destination_id, state, points, first_at, last_at, free_bytes,
                                                    slope_bytes_day, days_left, computed_at)
                     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
                    [$agentId, $taskId, $destinationId, $f['state'], count($points), Time::db($points[0][0]), Time::db(end($points)[0]),
                     (int) end($points)[1], $f['slope'] === null ? null : (string) round($f['slope'], 3), $f['days_left'], $computed]
                );
            }
        });
        return count($series);
    }

    /**
     * The trend of free space.
     *
     * @param list<array{0: int, 1: float}> $points [unix time, free bytes], oldest first
     * @return array{state: 'unknown'|'steady'|'filling', slope: ?float, days_left: ?int} slope in bytes per day
     */
    public static function forecast(array $points): array
    {
        $n = count($points);
        if ($n < self::MIN_POINTS || $points[$n - 1][0] - $points[0][0] < self::MIN_SPAN_S) {
            return ['state' => 'unknown', 'slope' => null, 'days_left' => null];
        }
        // Days since the first point keep the numbers small.
        $t0 = $points[0][0];
        $sx = $sy = $sxx = $sxy = 0.0;
        foreach ($points as [$time, $free]) {
            $x = ($time - $t0) / 86400;
            $sx += $x;
            $sy += $free;
            $sxx += $x * $x;
            $sxy += $x * $free;
        }
        $denominator = $n * $sxx - $sx * $sx;
        if ($denominator <= 0.0) {
            return ['state' => 'unknown', 'slope' => null, 'days_left' => null];
        }
        $slope = ($n * $sxy - $sx * $sy) / $denominator;
        if ($slope > -self::NOISE_BYTES_DAY) {
            return ['state' => 'steady', 'slope' => $slope, 'days_left' => null];
        }
        // From the latest measured free space, not from the line: that is what is really left now.
        $days = (int) floor($points[$n - 1][1] / -$slope);
        return ['state' => 'filling', 'slope' => $slope, 'days_left' => min(self::MAX_DAYS, max(0, $days))];
    }

    /**
     * Destinations of one machine as the agent last reported them, with the task, the forecast and the sets.
     *
     * @return list<array> rows of storage_space plus task_name, deleted, forecast (?array), soon (bool), sets (list), bad_sets (int)
     */
    public function forAgent(string $agentId): array
    {
        $rows = $this->db->all(
            'SELECT s.*, t.name AS task_name, t.deleted, t.data AS task_data
             FROM storage_space s
             JOIN (SELECT task_id, destination_id, MAX(id) AS id FROM storage_space WHERE agent_id = ? GROUP BY task_id, destination_id) last
               ON last.id = s.id
             LEFT JOIN tasks t ON t.agent_id = s.agent_id AND t.id = s.task_id
             ORDER BY t.name, s.path',
            [$agentId]
        );
        $forecasts = [];
        foreach ($this->db->all('SELECT * FROM storage_forecasts WHERE agent_id = ?', [$agentId]) as $f) {
            $forecasts[$f['task_id'] . '/' . $f['destination_id']] = $f;
        }
        $sets = [];
        foreach ($this->db->all(
            'SELECT * FROM storage_sets WHERE agent_id = ? ORDER BY set_time DESC, name DESC',
            [$agentId]
        ) as $set) {
            $sets[$set['task_id'] . '/' . $set['destination_id']][] = $set;
        }
        $result = [];
        foreach ($rows as $row) {
            // Destinations of deleted tasks, and destinations removed from a task, are no longer reported: skip them.
            if ($row['task_name'] === null || (int) $row['deleted'] === 1 || !self::taskHas($row['task_data'], $row['destination_id'])) {
                continue;
            }
            $key = $row['task_id'] . '/' . $row['destination_id'];
            unset($row['task_data']);
            $row['forecast'] = $forecasts[$key] ?? null;
            $row['soon'] = $this->soon($row['forecast']['state'] ?? null, $row['forecast']['days_left'] ?? null);
            $row['sets'] = $sets[$key] ?? [];
            $row['bad_sets'] = count(array_filter($row['sets'], fn($s) => $s['verify_ok'] !== null && (int) $s['verify_ok'] === 0));
            $result[] = $row;
        }
        return $result;
    }

    /**
     * For the dashboard (7.1): the destination with the least free space and the one that fills up first,
     * on approved machines and tasks that still exist.
     *
     * @return array{least: ?array, soonest: ?array}
     */
    public function summary(): array
    {
        $rows = $this->db->all(
            "SELECT s.agent_id, s.task_id, s.destination_id, s.kind, s.path, s.free_bytes, s.total_bytes, a.hostname, a.display_name,
                    t.data AS task_data, f.state, f.days_left
             FROM storage_space s
             JOIN (SELECT agent_id, task_id, destination_id, MAX(id) AS id FROM storage_space GROUP BY agent_id, task_id, destination_id) last
               ON last.id = s.id
             JOIN agents a ON a.id = s.agent_id AND a.status = 'approved'
             JOIN tasks t ON t.agent_id = s.agent_id AND t.id = s.task_id AND t.deleted = 0
             LEFT JOIN storage_forecasts f ON f.agent_id = s.agent_id AND f.task_id = s.task_id AND f.destination_id = s.destination_id
             WHERE s.free_bytes IS NOT NULL"
        );
        $least = $soonest = null;
        foreach ($rows as $row) {
            if (!self::taskHas($row['task_data'], $row['destination_id'])) {
                continue;
            }
            unset($row['task_data']);
            $row['soon'] = $this->soon($row['state'], $row['days_left']);
            if ($least === null || (int) $row['free_bytes'] < (int) $least['free_bytes']) {
                $least = $row;
            }
            if ($row['state'] === 'filling' && ($soonest === null || (int) $row['days_left'] < (int) $soonest['days_left'])) {
                $soonest = $row;
            }
        }
        return ['least' => $least, 'soonest' => $soonest];
    }

    /** Whether the task (its JSON) still has this destination. */
    private static function taskHas(?string $taskData, string $destinationId): bool
    {
        $task = json_decode((string) $taskData, true);
        if (!is_array($task)) {
            return false;
        }
        foreach ((array) ($task['destinations'] ?? []) as $d) {
            if (is_array($d) && strtolower((string) ($d['id'] ?? '')) === $destinationId) {
                return true;
            }
        }
        return false;
    }
}
