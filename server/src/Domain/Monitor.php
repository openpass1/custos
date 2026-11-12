<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * The heart of the system (section 6): presence of agents, expected schedule slots, missed runs, anomalies and
 * problems. Runs are reported through runStarted()/runFinished(); everything time-based happens in pass(), which
 * bin/cron.php calls every minute. Times are unix seconds so that tests can move the clock.
 */
final class Monitor
{
    /** A heartbeat older than this many heartbeat intervals means the agent's running/queue data is stale. */
    public const FRESH_INTERVALS = 3;
    public const MIN_FRESH_S = 180;
    /** A disabled agent checks in every 5 minutes: the heartbeat answer gives it this interval (A.4). */
    public const DISABLED_HEARTBEAT_S = 300;
    /** Anomalies (6.5) need at least this many earlier successful runs to compare with. */
    public const ANOMALY_MIN_RUNS = 3;
    public const ANOMALY_RUNS = 10;
    /** Safety limits for one pass. */
    private const MAX_SLOTS_PER_TASK = 1000;
    private const MAX_DECISIONS = 5000;

    public function __construct(private readonly Db $db, private readonly Settings $settings)
    {
    }

    /** How old a heartbeat may be for its running/queue data to count. */
    public function freshSeconds(): int
    {
        return max(self::MIN_FRESH_S, self::FRESH_INTERVALS * max(10, $this->settings->int('heartbeat_interval_s')));
    }

    public function graceSeconds(): int
    {
        return max(1, $this->settings->int('missed_grace_min')) * 60;
    }

    // ---- presence (6.1) ---------------------------------------------------------------------------------------

    /**
     * Extends the agent's current on-line period, or starts a new one after a gap of more than three intervals
     * (at least MIN_FRESH_S, so that a short interval and a few lost heartbeats do not cut the period into pieces).
     */
    public function presence(string $agentId, string $agentStatus, int $now): void
    {
        $interval = $agentStatus === 'disabled' ? self::DISABLED_HEARTBEAT_S : max(10, $this->settings->int('heartbeat_interval_s'));
        $gap = max(self::MIN_FRESH_S, self::FRESH_INTERVALS * $interval);
        $last = $this->db->one('SELECT id, to_at FROM agent_presence WHERE agent_id = ? ORDER BY to_at DESC LIMIT 1', [$agentId]);
        if ($last !== null && Time::unix($last['to_at']) >= $now - $gap) {
            if (Time::unix($last['to_at']) < $now) {
                $this->db->exec('UPDATE agent_presence SET to_at = ? WHERE id = ?', [Time::db($now), $last['id']]);
            }
            return;
        }
        $this->db->exec('INSERT INTO agent_presence (agent_id, from_at, to_at) VALUES (?, ?, ?)', [$agentId, Time::db($now), Time::db($now)]);
    }

    /** Whether the agent was on line at least once in [$from; $to]. */
    public function wasOnline(string $agentId, int $from, int $to): bool
    {
        return $this->db->value(
            'SELECT 1 FROM agent_presence WHERE agent_id = ? AND from_at <= ? AND to_at >= ? LIMIT 1',
            [$agentId, Time::db($to), Time::db($from)]
        ) !== null;
    }

    // ---- what is expected (6.2) -------------------------------------------------------------------------------

    /**
     * Expected runs of a task, or of every task of an agent, are counted from $now on: a new task, a changed
     * schedule, a task or machine that has just been enabled or approved.
     */
    public function watchFrom(string $agentId, ?string $taskId, int $now): void
    {
        $sql = 'UPDATE tasks SET watch_from = ?, slots_until = ? WHERE agent_id = ?';
        $params = [Time::db($now), Time::db($now), $agentId];
        if ($taskId !== null) {
            $sql .= ' AND id = ?';
            $params[] = $taskId;
        }
        $this->db->exec($sql, $params);
    }

    // ---- runs ------------------------------------------------------------------------------------------------

    /** runs/start: a run for slot T (a catch-up run, or a report that arrives late) settles that slot. */
    public function runStarted(string $agentId, string $taskId, string $runId, ?string $scheduledFor, int $now): void
    {
        if ($scheduledFor === null) {
            return;
        }
        $this->db->exec(
            "UPDATE expected_slots SET result = 'ok', run_id = ?, decided_at = ?
             WHERE agent_id = ? AND task_id = ? AND slot_at = ? AND result = 'pending'",
            [$runId, Time::db($now), $agentId, $taskId, $scheduledFor]
        );
        // missed_offline closes with a catch-up run for its slot (6.3). missed needs a successful run.
        $this->db->exec(
            "UPDATE expected_slots SET closed_at = ?, closed_by = 'catch_up', closed_run_id = ?
             WHERE agent_id = ? AND task_id = ? AND slot_at = ? AND result = 'missed_offline' AND closed_at IS NULL",
            [Time::db($now), $runId, $agentId, $taskId, $scheduledFor]
        );
    }

    /**
     * runs/finish: an error opens a problem or joins the open one; success or warning closes the error problem and
     * the missed slots before the run. Aborted runs change nothing (6.7).
     */
    public function runFinished(string $runId, int $now): void
    {
        $run = $this->db->one('SELECT * FROM runs WHERE id = ?', [$runId]);
        if ($run === null || $run['finished_at'] === null) {
            return;
        }
        if ($run['status'] === 'error') {
            $this->openProblem($run['agent_id'], $run['task_id'], 'error', $now, runId: $run['id'], message: self::firstLine($run['error_message']));
            return;
        }
        if ($run['status'] !== 'success' && $run['status'] !== 'warning') {
            return;
        }
        $this->checkAnomaly($run);
        $this->closeBySuccess($run['agent_id'], $run['task_id'], $run['id'], $run['started_at'], $now);
    }

    /** 6.5: source size and file count against the median of the last 10 successful runs. */
    private function checkAnomaly(array $run): void
    {
        $previous = $this->db->all(
            "SELECT source_files, source_bytes FROM runs
             WHERE agent_id = ? AND task_id = ? AND id <> ? AND status IN ('success', 'warning') AND finished_at IS NOT NULL
               AND started_at <= ?
             ORDER BY started_at DESC LIMIT " . self::ANOMALY_RUNS,
            [$run['agent_id'], $run['task_id'], $run['id'], $run['started_at']]
        );
        $drop = min(99.0, max(1.0, $this->settings->float('anomaly_drop_pct'))) / 100;
        $growth = max(1.01, $this->settings->float('anomaly_growth_factor'));
        $found = [];
        foreach (['source_files', 'source_bytes'] as $field) {
            $values = array_map('floatval', array_filter(array_column($previous, $field), fn($v) => $v !== null));
            if ($run[$field] === null || count($values) < self::ANOMALY_MIN_RUNS) {
                continue;
            }
            $median = self::median($values);
            $value = (float) $run[$field];
            if ($median > 0 && ($value < $median * (1 - $drop) || $value > $median * $growth)) {
                $found[$field] = [
                    'value' => (int) $value,
                    'median' => (int) round($median),
                    'change_pct' => (int) round(($value - $median) / $median * 100),
                ];
            }
        }
        $this->db->exec('UPDATE runs SET anomaly = ? WHERE id = ?',
            [$found === [] ? null : json_encode($found, JSON_UNESCAPED_SLASHES), $run['id']]);
    }

    private function closeBySuccess(string $agentId, string $taskId, string $runId, string $startedAt, int $now): void
    {
        $nowDb = Time::db($now);
        $this->db->exec(
            "UPDATE problems SET closed_at = ?, closed_by = 'run', closed_run_id = ?
             WHERE agent_id = ? AND task_id = ? AND kind = 'error' AND closed_at IS NULL",
            [$nowDb, $runId, $agentId, $taskId]
        );
        $this->db->exec(
            "UPDATE expected_slots SET closed_at = ?, closed_by = 'run', closed_run_id = ?
             WHERE agent_id = ? AND task_id = ? AND result IN ('missed', 'missed_offline') AND closed_at IS NULL AND slot_at <= ?",
            [$nowDb, $runId, $agentId, $taskId, $startedAt]
        );
        $stillMissed = $this->db->value(
            "SELECT 1 FROM expected_slots WHERE agent_id = ? AND task_id = ? AND result = 'missed' AND closed_at IS NULL LIMIT 1",
            [$agentId, $taskId]
        );
        if ($stillMissed === null) {
            $this->db->exec(
                "UPDATE problems SET closed_at = ?, closed_by = 'run', closed_run_id = ?
                 WHERE agent_id = ? AND task_id = ? AND kind = 'missed' AND closed_at IS NULL",
                [$nowDb, $runId, $agentId, $taskId]
            );
        }
    }

    /**
     * Opens a problem, or adds one more occurrence to the open problem of the same kind (6.7). The unique key on
     * open problems makes this one atomic statement, so the API and cron cannot open two at once.
     */
    public function openProblem(
        string $agentId,
        string $taskId,
        string $kind,
        int $now,
        ?string $runId = null,
        ?string $slotAt = null,
        ?string $slotKind = null,
        ?string $message = null
    ): void {
        $nowDb = Time::db($now);
        $this->db->exec(
            'INSERT INTO problems (agent_id, task_id, kind, opened_at, last_at, first_run_id, last_run_id, first_slot_at, last_slot_at, slot_kind, message)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
             ON DUPLICATE KEY UPDATE occurrences = occurrences + 1, last_at = ?, last_run_id = COALESCE(?, last_run_id),
                                     last_slot_at = COALESCE(?, last_slot_at)',
            [$agentId, $taskId, $kind, $nowDb, $nowDb, $runId, $runId, $slotAt, $slotAt, $slotKind, $message,
             $nowDb, $runId, $slotAt]
        );
    }

    // ---- the cron pass ---------------------------------------------------------------------------------------

    /** @return array{slots: int, missed: int, missed_offline: int, interval_missed: int} */
    public function pass(int $now): array
    {
        $this->sweep(null, $now);
        $stats = ['slots' => $this->createSlots($now)];
        $stats += $this->decideSlots($now);
        $stats['interval_missed'] = $this->checkIntervals($now);
        return $stats;
    }

    /**
     * Closes what no longer counts: problems and missed slots of disabled or deleted tasks, of tasks that are now
     * started by hand only, and of machines that are not approved; drops their pending slots (6.2, 6.7).
     */
    public function sweep(?string $agentId, int $now): void
    {
        $filter = $agentId === null ? '' : ' AND x.agent_id = ?';
        $params = $agentId === null ? [] : [$agentId];
        $join = ' JOIN agents a ON a.id = x.agent_id LEFT JOIN tasks t ON t.agent_id = x.agent_id AND t.id = x.task_id';
        $problems = $this->db->all(
            "SELECT x.id, x.kind, a.status AS agent_status, t.id AS task, t.enabled, t.deleted, t.data FROM problems x $join
             WHERE x.closed_at IS NULL$filter",
            $params
        );
        foreach ($problems as $row) {
            $reason = self::notExpected($row);
            // A task started by hand has no slots to miss, but its failed runs are still errors.
            if ($reason !== null && ($reason !== 'unscheduled' || $row['kind'] === 'missed')) {
                $this->db->exec('UPDATE problems SET closed_at = ?, closed_by = ? WHERE id = ? AND closed_at IS NULL',
                    [Time::db($now), $reason, $row['id']]);
            }
        }
        $slots = $this->db->all(
            "SELECT x.agent_id, x.task_id, x.slot_at, x.result, a.status AS agent_status, t.id AS task, t.enabled, t.deleted, t.data
             FROM expected_slots x $join
             WHERE (x.result = 'pending' OR (x.result IN ('missed', 'missed_offline') AND x.closed_at IS NULL))$filter",
            $params
        );
        foreach ($slots as $row) {
            if (($reason = self::notExpected($row)) === null) {
                continue;
            }
            $key = [$row['agent_id'], $row['task_id'], $row['slot_at']];
            if ($row['result'] === 'pending') {
                $this->db->exec('DELETE FROM expected_slots WHERE agent_id = ? AND task_id = ? AND slot_at = ? AND result = \'pending\'', $key);
            } else {
                $this->db->exec('UPDATE expected_slots SET closed_at = ?, closed_by = ? WHERE agent_id = ? AND task_id = ? AND slot_at = ?',
                    [Time::db($now), $reason, ...$key]);
            }
        }
    }

    /** Why runs of this task are not expected any more, or null when they are. A task the server has not seen yet counts as expected. */
    private static function notExpected(array $row): ?string
    {
        return match (true) {
            $row['agent_status'] !== 'approved' => 'agent_disabled',
            $row['task'] === null => null,
            (bool) $row['deleted'] => 'task_deleted',
            !$row['enabled'] => 'task_disabled',
            !Schedule::isExpected(self::schedule($row['data'])) => 'unscheduled',
            default => null,
        };
    }

    /** Adds the slots that have come since the last pass. */
    private function createSlots(int $now): int
    {
        $count = 0;
        foreach ($this->expectedTasks() as $task) {
            if ($task['slots_until'] === null || $task['watch_from'] === null) {
                // A task from before this version: expectations start now rather than in the past.
                $this->watchFrom($task['agent_id'], $task['id'], $now);
                continue;
            }
            $schedule = self::schedule($task['data']);
            if (!Schedule::hasSlots($schedule)) {
                continue;
            }
            $from = max(Time::unix($task['slots_until']), Time::unix($task['watch_from']));
            if ($from >= $now) {
                continue;
            }
            foreach (Schedule::between($schedule, $task['time_zone'], $from, $now, self::MAX_SLOTS_PER_TASK) as $slot) {
                $count += $this->db->exec(
                    'INSERT IGNORE INTO expected_slots (agent_id, task_id, slot_at, kind) VALUES (?, ?, ?, \'schedule\')',
                    [$task['agent_id'], $task['id'], Time::db($slot)]
                );
            }
            $this->db->exec('UPDATE tasks SET slots_until = ? WHERE agent_id = ? AND id = ? AND slots_until = ?',
                [Time::db($now), $task['agent_id'], $task['id'], $task['slots_until']]);
        }
        return $count;
    }

    /**
     * 6.3: every pending slot T whose grace period is over becomes ok (a run for it exists), waits (the task is queued,
     * running or paused on the agent), missed (the machine was on line in [T; T + grace]) or missed_offline.
     *
     * @return array{missed: int, missed_offline: int}
     */
    private function decideSlots(int $now): array
    {
        $grace = $this->graceSeconds();
        $stats = ['missed' => 0, 'missed_offline' => 0];
        $slots = $this->db->all(
            "SELECT s.agent_id, s.task_id, s.slot_at, a.last_heartbeat_at, a.running, a.queue
             FROM expected_slots s JOIN agents a ON a.id = s.agent_id
             WHERE s.result = 'pending' AND s.slot_at <= ?
             ORDER BY s.slot_at LIMIT " . self::MAX_DECISIONS,
            [Time::db($now - $grace)]
        );
        foreach ($slots as $slot) {
            $at = Time::unix($slot['slot_at']);
            $key = [$slot['agent_id'], $slot['task_id'], $slot['slot_at']];
            $runId = $this->runForSlot($slot['agent_id'], $slot['task_id'], $at, $grace);
            if ($runId !== null) {
                $this->db->exec(
                    "UPDATE expected_slots SET result = 'ok', run_id = ?, decided_at = ? WHERE agent_id = ? AND task_id = ? AND slot_at = ?",
                    [$runId, Time::db($now), ...$key]
                );
                continue;
            }
            if ($this->isBusyWith($slot, $slot['task_id'], $now)) {
                continue;
            }
            $result = $this->wasOnline($slot['agent_id'], $at, $at + $grace) ? 'missed' : 'missed_offline';
            // A successful run after the slot that is already known (a late report) closes it right away.
            $success = $this->db->value(
                "SELECT id FROM runs WHERE agent_id = ? AND task_id = ? AND started_at >= ? AND status IN ('success', 'warning')
                 ORDER BY started_at LIMIT 1",
                $key
            );
            $this->db->exec(
                'UPDATE expected_slots SET result = ?, decided_at = ?, closed_at = ?, closed_by = ?, closed_run_id = ?
                 WHERE agent_id = ? AND task_id = ? AND slot_at = ? AND result = \'pending\'',
                [$result, Time::db($now), $success === null ? null : Time::db($now), $success === null ? null : 'run', $success, ...$key]
            );
            $stats[$result]++;
            if ($result === 'missed' && $success === null) {
                $this->openProblem($slot['agent_id'], $slot['task_id'], 'missed', $now, slotAt: $slot['slot_at'], slotKind: 'schedule');
            }
        }
        return $stats;
    }

    /**
     * Rule 1 of 6.3: a run with scheduled_for = T, a run that started in [T − 1 min; T + grace], or a run that was
     * already in progress at T (the agent does not start a second copy of a running task).
     */
    private function runForSlot(string $agentId, string $taskId, int $at, int $grace): ?string
    {
        $id = $this->db->value('SELECT id FROM runs WHERE agent_id = ? AND task_id = ? AND scheduled_for = ? LIMIT 1',
            [$agentId, $taskId, Time::db($at)]);
        $id ??= $this->db->value(
            'SELECT id FROM runs WHERE agent_id = ? AND task_id = ? AND started_at BETWEEN ? AND ? ORDER BY started_at LIMIT 1',
            [$agentId, $taskId, Time::db($at - 60), Time::db($at + $grace)]
        );
        $id ??= $this->db->value(
            'SELECT id FROM runs WHERE agent_id = ? AND task_id = ? AND started_at < ? AND started_at >= ? AND finished_at >= ?
             ORDER BY started_at DESC LIMIT 1',
            [$agentId, $taskId, Time::db($at), Time::db($at - 7 * 86400), Time::db($at)]
        );
        return $id;
    }

    /** Rule 2 of 6.3: the task is queued, running or paused according to a recent heartbeat. */
    private function isBusyWith(array $agent, string $taskId, int $now): bool
    {
        if ($agent['last_heartbeat_at'] === null || Time::unix($agent['last_heartbeat_at']) < $now - $this->freshSeconds()) {
            return false;
        }
        $running = json_decode((string) $agent['running'], true);
        if (is_array($running) && ($running['task_id'] ?? null) === $taskId) {
            return true;
        }
        return in_array($taskId, (array) json_decode((string) $agent['queue'], true), true);
    }

    /**
     * Timer tasks (6.3, last paragraph): the agent has been on line without a break for longer than interval + grace
     * and the task has not started in that time. One open "missed" slot per task until a successful run closes it.
     */
    private function checkIntervals(int $now): int
    {
        $grace = $this->graceSeconds();
        $fresh = $this->freshSeconds();
        $count = 0;
        foreach ($this->expectedTasks() as $task) {
            $interval = Schedule::interval(self::schedule($task['data']));
            if ($interval === null || $task['watch_from'] === null) {
                continue;
            }
            if ($task['last_heartbeat_at'] === null || Time::unix($task['last_heartbeat_at']) < $now - $fresh
                || $this->isBusyWith($task, $task['id'], $now)) {
                continue;
            }
            $windowStart = $now - $interval - $grace;
            $lastStart = $this->db->value('SELECT MAX(started_at) FROM runs WHERE agent_id = ? AND task_id = ?', [$task['agent_id'], $task['id']]);
            $base = max(Time::unix($task['watch_from']), $lastStart === null ? 0 : Time::unix($lastStart));
            if ($base > $windowStart) {
                continue;
            }
            $online = $this->db->value(
                'SELECT from_at FROM agent_presence WHERE agent_id = ? AND from_at <= ? AND to_at >= ? ORDER BY to_at DESC LIMIT 1',
                [$task['agent_id'], Time::db($windowStart), Time::db($now - $fresh)]
            );
            if ($online === null) {
                continue;
            }
            $open = $this->db->value(
                "SELECT 1 FROM expected_slots WHERE agent_id = ? AND task_id = ? AND kind = 'interval' AND result = 'missed' AND closed_at IS NULL LIMIT 1",
                [$task['agent_id'], $task['id']]
            );
            if ($open !== null) {
                continue;
            }
            $slotAt = Time::db(max($base, Time::unix($online)) + $interval);
            $inserted = $this->db->exec(
                "INSERT IGNORE INTO expected_slots (agent_id, task_id, slot_at, kind, result, decided_at)
                 VALUES (?, ?, ?, 'interval', 'missed', ?)",
                [$task['agent_id'], $task['id'], $slotAt, Time::db($now)]
            );
            if ($inserted > 0) {
                $this->openProblem($task['agent_id'], $task['id'], 'missed', $now, slotAt: $slotAt, slotKind: 'interval');
                $count++;
            }
        }
        return $count;
    }

    /** Enabled, not deleted tasks of approved agents, with what the checks need from the agent. */
    private function expectedTasks(): array
    {
        return $this->db->all(
            "SELECT t.agent_id, t.id, t.data, t.watch_from, t.slots_until, a.time_zone, a.last_heartbeat_at, a.running, a.queue
             FROM tasks t JOIN agents a ON a.id = t.agent_id
             WHERE a.status = 'approved' AND t.deleted = 0 AND t.enabled = 1"
        );
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    public static function schedule(?string $taskJson): mixed
    {
        $data = json_decode((string) $taskJson, true);
        return is_array($data) ? ($data['schedule'] ?? null) : null;
    }

    public static function firstLine(?string $text): ?string
    {
        if ($text === null) {
            return null;
        }
        $line = trim((string) strtok(trim($text), "\r\n"));
        return $line === '' ? null : mb_substr($line, 0, 500);
    }

    /** @param list<float> $values */
    private static function median(array $values): float
    {
        sort($values);
        $n = count($values);
        $mid = intdiv($n, 2);
        return $n % 2 === 1 ? $values[$mid] : ($values[$mid - 1] + $values[$mid]) / 2;
    }
}
