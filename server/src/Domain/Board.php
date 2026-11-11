<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * What the pages show: machines with their on-line state and every task with its resulting status (6.6), open
 * problems and soft marks. Computed on every request from a handful of queries, so it is never out of date.
 */
final class Board
{
    /** 6.6, most important first. "disabled" is the exception: a disabled task shows only running/paused above it. */
    public const ORDER = ['running', 'paused', 'error', 'missed', 'warning', 'anomaly', 'missed_offline', 'stale', 'ok', 'never', 'disabled'];

    /** @var array<string, array> agent id => agent row with name, online, fresh, running, queue */
    public array $agents = [];
    /** @var array<string, array> "agent_id/task_id" => task row with status, flags, next_at, problems, offline_slots */
    public array $tasks = [];
    /** @var list<array> open problems, errors first, newest first */
    public array $problems = [];

    private function __construct(public readonly int $now)
    {
    }

    public static function load(Db $db, Settings $settings, Monitor $monitor, int $now, ?string $agentId = null): self
    {
        $board = new self($now);
        $offlineAfter = $settings->int('offline_after_min');
        $fresh = $monitor->freshSeconds();
        $staleBefore = $now - max(1, $settings->int('stale_days')) * 86400;
        $where = $agentId === null ? '' : ' WHERE a.id = ?';
        $params = $agentId === null ? [] : [$agentId];

        foreach ($db->all("SELECT a.*, g.name AS group_name FROM agents a LEFT JOIN agent_groups g ON g.id = a.group_id$where", $params) as $agent) {
            $agent['name'] = Agents::name($agent);
            $agent['online'] = $agent['status'] !== 'pending' && Agents::isOnline($agent, $offlineAfter);
            $agent['fresh'] = $agent['last_heartbeat_at'] !== null && Time::unix($agent['last_heartbeat_at']) >= $now - $fresh;
            $running = json_decode((string) $agent['running'], true);
            $agent['running_task'] = $agent['fresh'] && is_array($running) ? ($running['task_id'] ?? null) : null;
            $agent['running_run'] = $agent['running_task'] === null ? null : ($running['run_id'] ?? null);
            $agent['paused_at'] = $agent['running_task'] === null ? null : Time::parseIso((string) ($running['paused_at'] ?? ''));
            $agent['queue_ids'] = $agent['fresh'] ? array_values(array_filter((array) json_decode((string) $agent['queue'], true), 'is_string')) : [];
            $board->agents[$agent['id']] = $agent;
        }

        $taskWhere = $agentId === null ? '' : ' AND agent_id = ?';
        foreach ($db->all("SELECT * FROM tasks WHERE deleted = 0$taskWhere ORDER BY name", $params) as $task) {
            if (!isset($board->agents[$task['agent_id']])) {
                continue;
            }
            $data = json_decode($task['data'], true);
            $task['def'] = is_array($data) ? $data : [];
            $task['schedule'] = $task['def']['schedule'] ?? null;
            $task['problems'] = [];
            $task['offline_slots'] = [];
            $board->tasks[$task['agent_id'] . '/' . $task['id']] = $task;
        }

        $problemWhere = $agentId === null ? '' : ' AND p.agent_id = ?';
        foreach ($db->all(
            "SELECT p.* FROM problems p WHERE p.closed_at IS NULL$problemWhere ORDER BY p.kind = 'error' DESC, p.opened_at DESC",
            $params
        ) as $problem) {
            if (!isset($board->agents[$problem['agent_id']])) {
                continue;
            }
            $key = $problem['agent_id'] . '/' . $problem['task_id'];
            if (isset($board->tasks[$key])) {
                $board->tasks[$key]['problems'][$problem['kind']] = $problem;
            }
            $problem['agent'] = $board->agents[$problem['agent_id']];
            $problem['task_name'] = $board->tasks[$key]['name'] ?? null;
            $problem['time_zone'] = $board->agents[$problem['agent_id']]['time_zone'];
            $board->problems[] = $problem;
        }

        $slotWhere = $agentId === null ? '' : ' AND agent_id = ?';
        foreach ($db->all(
            "SELECT agent_id, task_id, slot_at FROM expected_slots WHERE result = 'missed_offline' AND closed_at IS NULL$slotWhere ORDER BY slot_at",
            $params
        ) as $slot) {
            $key = $slot['agent_id'] . '/' . $slot['task_id'];
            if (isset($board->tasks[$key])) {
                $board->tasks[$key]['offline_slots'][] = $slot['slot_at'];
            }
        }

        foreach ($board->tasks as $key => $task) {
            $board->tasks[$key] = $board->evaluate($task, $board->agents[$task['agent_id']], $staleBefore);
        }
        return $board;
    }

    /** Status, active flags and the next expected start of one task. */
    private function evaluate(array $task, array $agent, int $staleBefore): array
    {
        $watched = $agent['status'] === 'approved' && $task['enabled'] && Schedule::isExpected($task['schedule']);
        $flags = [];
        if ($agent['running_task'] === $task['id']) {
            $flags[] = $agent['state'] === 'paused' || $agent['paused_at'] !== null ? 'paused' : 'running';
        }
        if (!$task['enabled'] || $agent['status'] !== 'approved') {
            $flags[] = 'disabled';
        }
        // A failed last run is an error even when its problem was closed by disabling the task or the machine.
        if (isset($task['problems']['error']) || $task['last_result'] === 'error') {
            $flags[] = 'error';
        }
        if (isset($task['problems']['missed'])) {
            $flags[] = 'missed';
        }
        if ($task['last_result'] === 'warning') {
            $flags[] = 'warning';
        }
        if ($task['anomaly']) {
            $flags[] = 'anomaly';
        }
        if ($task['offline_slots'] !== []) {
            $flags[] = 'missed_offline';
        }
        $reference = $task['last_success_at'] ?? $task['watch_from'] ?? $task['first_seen_at'];
        if ($watched && Time::unix($reference) < $staleBefore) {
            $flags[] = 'stale';
        }
        $flags[] = $task['last_result'] === null ? 'never' : 'ok';

        $task['flags'] = $flags;
        $task['status'] = self::pick($flags);
        $task['watched'] = $watched;
        $task['queued'] = in_array($task['id'], $agent['queue_ids'], true);
        $task['next_at'] = $watched ? $this->nextStart($task, $agent) : null;
        return $task;
    }

    /** The resulting status from the active flags (6.6). */
    public static function pick(array $flags): string
    {
        foreach (['running', 'paused'] as $status) {
            if (in_array($status, $flags, true)) {
                return $status;
            }
        }
        if (in_array('disabled', $flags, true)) {
            return 'disabled';
        }
        foreach (self::ORDER as $status) {
            if (in_array($status, $flags, true)) {
                return $status;
            }
        }
        return 'never';
    }

    private function nextStart(array $task, array $agent): ?int
    {
        $interval = Schedule::interval($task['schedule']);
        if ($interval !== null) {
            $base = max(
                $task['last_run_at'] === null ? 0 : Time::unix($task['last_run_at']),
                $task['watch_from'] === null ? $this->now : Time::unix($task['watch_from'])
            );
            return max($this->now, $base + $interval);
        }
        return Schedule::next($task['schedule'], $agent['time_zone'], $this->now);
    }

    /** Tasks of one agent, in name order. */
    public function tasksOf(string $agentId): array
    {
        return array_values(array_filter($this->tasks, fn($t) => $t['agent_id'] === $agentId));
    }

    public function task(string $agentId, string $taskId): ?array
    {
        return $this->tasks[$agentId . '/' . $taskId] ?? null;
    }

    /**
     * Dashboard figures (7.1): machines and tasks of approved machines by status.
     *
     * @return array<string, int>
     */
    public function summary(): array
    {
        $s = ['agents' => 0, 'online' => 0, 'pending' => 0, 'disabled_agents' => 0,
              'ok' => 0, 'warning' => 0, 'error' => 0, 'missed' => 0, 'running' => 0, 'soft' => 0, 'tasks' => 0];
        foreach ($this->agents as $agent) {
            match ($agent['status']) {
                'pending' => $s['pending']++,
                'disabled' => $s['disabled_agents']++,
                default => null,
            };
            if ($agent['status'] !== 'pending') {
                $s['agents']++;
            }
            if ($agent['status'] === 'approved' && $agent['online']) {
                $s['online']++;
            }
        }
        foreach ($this->tasks as $task) {
            if ($this->agents[$task['agent_id']]['status'] !== 'approved' || $task['status'] === 'disabled') {
                continue;
            }
            $s['tasks']++;
            match ($task['status']) {
                'ok' => $s['ok']++,
                'warning', 'anomaly' => $s['warning']++,
                'error' => $s['error']++,
                'missed' => $s['missed']++,
                'running', 'paused' => $s['running']++,
                'missed_offline', 'stale' => $s['soft']++,
                default => null,
            };
        }
        return $s;
    }

    /**
     * Soft marks for the calm block under the problems (7.1): machines that were off at their slots, tasks without a
     * recent successful copy, anomalies and paused runs.
     *
     * @return array{offline: list<array{agent: array, tasks: list<array>}>, stale: list<array>, anomaly: list<array>, paused: list<array>}
     */
    public function softMarks(): array
    {
        $offline = [];
        $stale = $anomaly = $paused = [];
        foreach ($this->tasks as $task) {
            $agent = $this->agents[$task['agent_id']];
            if ($agent['status'] !== 'approved') {
                continue;
            }
            if (in_array('missed_offline', $task['flags'], true)) {
                $offline[$agent['id']]['agent'] = $agent;
                $offline[$agent['id']]['tasks'][] = $task;
            }
            if (in_array('stale', $task['flags'], true)) {
                $stale[] = $task + ['agent' => $agent];
            }
            if (in_array('anomaly', $task['flags'], true)) {
                $anomaly[] = $task + ['agent' => $agent];
            }
            if ($task['status'] === 'paused') {
                $paused[] = $task + ['agent' => $agent];
            }
        }
        return ['offline' => array_values($offline), 'stale' => $stale, 'anomaly' => $anomaly, 'paused' => $paused];
    }

    /**
     * Runs in progress (7.1): fresh agents that report a running or paused task, with the latest progress.
     *
     * @return list<array{agent: array, task: ?array, progress: ?array}>
     */
    public function running(Db $db): array
    {
        $result = [];
        foreach ($this->agents as $agent) {
            if ($agent['status'] !== 'approved' || $agent['running_task'] === null) {
                continue;
            }
            $progress = $db->one('SELECT * FROM run_progress WHERE agent_id = ? AND run_id = ?', [$agent['id'], (string) $agent['running_run']]);
            $result[] = ['agent' => $agent, 'task' => $this->task($agent['id'], $agent['running_task']), 'progress' => $progress];
        }
        usort($result, fn($a, $b) => strcmp($a['agent']['name'], $b['agent']['name']));
        return $result;
    }
}
