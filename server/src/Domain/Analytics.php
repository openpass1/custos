<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * History for people: the 30-day heat map of the dashboard (7.1) and the charts of the task page (7.4).
 */
final class Analytics
{
    public const HEATMAP_DAYS = 30;
    /** Results of a day from the worst down; a day without any of them is empty. */
    public const DAY_ORDER = ['error', 'missed', 'warning', 'missed_offline', 'success'];
    public const CHART_DAYS = 90;
    public const CHART_RUNS = 200;

    public function __construct(private readonly Db $db)
    {
    }

    /**
     * The heat map: one row per approved machine and a row for all of them, one cell per day in the site time zone.
     * A cell is the worst result of the day: failed runs, missed slots, runs with warnings, slots missed while the
     * machine was off, successful runs. Aborted runs and runs still going do not count.
     *
     * @return array{days: list<string>, total: array<string, array>, rows: list<array{agent: array, cells: array<string, array>}>}
     *         days as "Y-m-d", oldest first; a cell is {worst: ?string, counts: array<string, int>}
     */
    public function heatmap(int $now, string $timeZone, int $days = self::HEATMAP_DAYS): array
    {
        $zone = new \DateTimeZone($timeZone);
        $today = (new \DateTimeImmutable('@' . $now))->setTimezone($zone)->setTime(0, 0);
        $first = $today->modify('-' . ($days - 1) . ' days');
        $dayList = [];
        for ($day = $first; $day <= $today; $day = $day->modify('+1 day')) {
            $dayList[] = $day->format('Y-m-d');
        }
        $since = Time::db($first->getTimestamp());

        $agents = [];
        foreach ($this->db->all("SELECT a.*, g.name AS group_name FROM agents a LEFT JOIN agent_groups g ON g.id = a.group_id WHERE a.status = 'approved'") as $agent) {
            $agent['name'] = Agents::name($agent);
            $agents[$agent['id']] = $agent;
        }
        $counts = [];
        $add = function (string $agentId, string $utc, string $result) use (&$counts, $agents, $zone): void {
            if (!isset($agents[$agentId])) {
                return;
            }
            $day = (new \DateTimeImmutable($utc, new \DateTimeZone('UTC')))->setTimezone($zone)->format('Y-m-d');
            $counts[$agentId][$day][$result] = ($counts[$agentId][$day][$result] ?? 0) + 1;
        };
        // Runs of tasks that still exist (a deleted task keeps its history until the retention period, but its day
        // would stay red forever on a map nobody can fix).
        foreach ($this->db->all(
            "SELECT r.agent_id, r.started_at, r.status FROM runs r
             JOIN tasks t ON t.agent_id = r.agent_id AND t.id = r.task_id
             WHERE r.started_at >= ? AND r.status IN ('success', 'warning', 'error')",
            [$since]
        ) as $run) {
            $add($run['agent_id'], $run['started_at'], $run['status']);
        }
        foreach ($this->db->all(
            "SELECT agent_id, slot_at, result FROM expected_slots WHERE slot_at >= ? AND result IN ('missed', 'missed_offline')",
            [$since]
        ) as $slot) {
            $add($slot['agent_id'], $slot['slot_at'], $slot['result']);
        }

        $rows = [];
        $total = [];
        uasort($agents, fn($a, $b) => strcasecmp($a['name'], $b['name']));
        foreach ($agents as $id => $agent) {
            $cells = [];
            foreach ($dayList as $day) {
                $c = $counts[$id][$day] ?? [];
                $cells[$day] = ['worst' => self::worst($c), 'counts' => $c];
                foreach ($c as $result => $n) {
                    $total[$day][$result] = ($total[$day][$result] ?? 0) + $n;
                }
            }
            $rows[] = ['agent' => $agent, 'cells' => $cells];
        }
        $totalCells = [];
        foreach ($dayList as $day) {
            $totalCells[$day] = ['worst' => self::worst($total[$day] ?? []), 'counts' => $total[$day] ?? []];
        }
        return ['days' => $dayList, 'total' => $totalCells, 'rows' => $rows];
    }

    /** @param array<string, int> $counts */
    public static function worst(array $counts): ?string
    {
        foreach (self::DAY_ORDER as $result) {
            if (($counts[$result] ?? 0) > 0) {
                return $result;
            }
        }
        return null;
    }

    /**
     * Points of the task charts: finished runs of the last 90 days (at most 200), oldest first. Aborted runs are left
     * out: they say nothing about the source or the speed.
     *
     * @return list<array{id: string, at: string, status: string, source_bytes: ?int, source_files: ?int, written: ?int, duration: ?int, anomaly: bool}>
     */
    public function taskChart(string $agentId, string $taskId, int $now): array
    {
        $rows = $this->db->all(
            "SELECT id, started_at, status, source_bytes, source_files, bytes_written, duration_s, anomaly FROM runs
             WHERE agent_id = ? AND task_id = ? AND started_at >= ? AND finished_at IS NOT NULL AND status <> 'aborted'
             ORDER BY started_at DESC LIMIT " . self::CHART_RUNS,
            [$agentId, $taskId, Time::db($now - self::CHART_DAYS * 86400)]
        );
        $int = fn($v) => $v === null ? null : (int) $v;
        return array_reverse(array_map(fn($r) => [
            'id' => $r['id'],
            'at' => $r['started_at'],
            'status' => $r['status'],
            'source_bytes' => $int($r['source_bytes']),
            'source_files' => $int($r['source_files']),
            'written' => $int($r['bytes_written']),
            'duration' => $int($r['duration_s']),
            'anomaly' => $r['anomaly'] !== null,
        ], $rows));
    }
}
