<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Data for Zabbix (section 10) and the tokens of Zabbix and the wall screen (7.9).
 *
 * Tasks are keyed by "<agent id>/<task id>" ({#TASK_KEY}), not by the task id alone: a machine registered anew next to
 * its old record (and a configuration copied between machines) has tasks with the same ids on two machines.
 */
final class Integrations
{
    public const TOKENS = ['zabbix_token', 'wall_token'];

    /**
     * status_code for Zabbix triggers (section 10). A run in progress (1) is shown only instead of "ok" or "not run
     * yet": an error or a missed run stays visible while the next run goes, so triggers do not flap.
     */
    public const CODES = ['ok' => 0, 'running' => 1, 'paused' => 1, 'warning' => 2, 'anomaly' => 2, 'missed_offline' => 3,
                          'stale' => 4, 'missed' => 5, 'error' => 6, 'disabled' => 9, 'never' => 9];

    public function __construct(private readonly Db $db, private readonly Settings $settings)
    {
    }

    public static function newToken(): string
    {
        return bin2hex(random_bytes(24));
    }

    /** Creates the tokens that do not exist yet (install.php, migrate.php). Returns the names of the created ones. */
    public function ensureTokens(): array
    {
        $created = [];
        foreach (self::TOKENS as $name) {
            if ($this->settings->get($name) === '') {
                $this->settings->set($name, self::newToken());
                $created[] = $name;
            }
        }
        return $created;
    }

    /** Compares a token in constant time; an empty stored token lets nobody in. */
    public function tokenMatches(string $name, string $given): bool
    {
        $stored = $this->settings->get($name);
        return $stored !== '' && $given !== '' && hash_equals($stored, $given);
    }

    /** @return list<array<string, string>> {#AGENT_ID}, {#AGENT}, {#GROUP} of approved machines */
    public static function discoverAgents(Board $board): array
    {
        $result = [];
        foreach ($board->agents as $agent) {
            if ($agent['status'] === 'approved') {
                $result[] = ['{#AGENT_ID}' => $agent['id'], '{#AGENT}' => $agent['name'], '{#GROUP}' => (string) ($agent['group_name'] ?? '')];
            }
        }
        usort($result, fn($a, $b) => strcasecmp($a['{#AGENT}'], $b['{#AGENT}']));
        return $result;
    }

    /** @return list<array<string, string>> the tasks of approved machines */
    public static function discoverTasks(Board $board): array
    {
        $result = [];
        foreach ($board->tasks as $task) {
            $agent = $board->agents[$task['agent_id']];
            if ($agent['status'] !== 'approved') {
                continue;
            }
            $result[] = [
                '{#AGENT_ID}' => $agent['id'], '{#AGENT}' => $agent['name'],
                '{#TASK_ID}' => $task['id'], '{#TASK}' => $task['name'],
                '{#TASK_KEY}' => $agent['id'] . '/' . $task['id'],
            ];
        }
        usort($result, fn($a, $b) => [$a['{#AGENT}'], $a['{#TASK}']] <=> [$b['{#AGENT}'], $b['{#TASK}']]);
        return $result;
    }

    /**
     * The code of a task for Zabbix: the most serious active flag, so that every trigger sees its own problem (a stale
     * task with a warning is 4, not 2). Running only replaces ok and "not run yet"; a disabled task is always 9.
     */
    public static function code(array $task): int
    {
        $flags = $task['flags'];
        if (in_array('disabled', $flags, true)) {
            return self::CODES['disabled'];
        }
        $code = null;
        foreach ($flags as $flag) {
            if (!in_array($flag, ['running', 'paused', 'ok', 'never'], true) && isset(self::CODES[$flag])) {
                $code = max($code ?? 0, self::CODES[$flag]);
            }
        }
        if ($code !== null) {
            return $code;
        }
        if (in_array('running', $flags, true) || in_array('paused', $flags, true)) {
            return self::CODES['running'];
        }
        return in_array('never', $flags, true) ? self::CODES['never'] : self::CODES['ok'];
    }

    /** /api/zabbix/status: every status in one answer. */
    public function status(Board $board): array
    {
        $now = $board->now;
        $agents = [];
        foreach ($board->agents as $agent) {
            if ($agent['status'] !== 'approved') {
                continue;
            }
            $seen = $agent['last_heartbeat_at'] ?? $agent['approved_at'] ?? $agent['first_seen_at'];
            $agents[$agent['id']] = [
                'name' => $agent['name'],
                'online' => $agent['online'] ? 1 : 0,
                'last_seen_s' => max(0, $now - Time::unix($seen)),
                'version' => (string) $agent['agent_version'],
            ];
        }
        $last = [];
        foreach ($this->db->all(
            'SELECT r.agent_id, r.task_id, r.status, r.bytes_written FROM runs r JOIN tasks t ON t.last_result_run_id = r.id'
        ) as $run) {
            $last[$run['agent_id'] . '/' . $run['task_id']] = $run;
        }
        $tasks = [];
        foreach ($board->tasks as $key => $task) {
            if (!isset($agents[$task['agent_id']])) {
                continue;
            }
            $tasks[$key] = [
                'name' => $task['name'],
                'status' => $task['status'],
                'status_code' => self::code($task),
                'last_success_age_s' => $task['last_success_at'] === null ? -1 : max(0, $now - Time::unix($task['last_success_at'])),
                'last_run_status' => (string) ($last[$key]['status'] ?? ''),
                'last_bytes_written' => (int) ($last[$key]['bytes_written'] ?? 0),
                'anomaly' => in_array('anomaly', $task['flags'], true) ? 1 : 0,
            ];
        }
        $summary = $board->summary();
        return [
            'time' => Time::iso(Time::db($now)),
            'agents' => (object) $agents,
            'tasks' => (object) $tasks,
            'summary' => [
                'agents' => $summary['agents'], 'online' => $summary['online'], 'pending' => $summary['pending'],
                'tasks' => $summary['tasks'], 'ok' => $summary['ok'], 'warning' => $summary['warning'], 'error' => $summary['error'],
                'missed' => $summary['missed'], 'running' => $summary['running'], 'soft' => $summary['soft'],
                'problems' => count($board->problems),
            ],
        ];
    }

    /**
     * /api/health: whether the database answers and how long ago cron ran.
     *
     * @return array{0: int, 1: array} HTTP status and body
     */
    public static function health(callable $db, int $now, int $cronLateS): array
    {
        try {
            /** @var Db $connection */
            $connection = $db();
            $connection->value('SELECT 1');
            $last = (string) ($connection->value("SELECT value FROM settings WHERE name = 'cron_last_run'") ?? '');
        } catch (\Throwable) {
            return [503, ['status' => 'fail', 'db' => 'fail', 'cron_lag_s' => null]];
        }
        $lag = $last === '' ? null : max(0, $now - Time::unix($last));
        $ok = $lag !== null && $lag <= $cronLateS;
        return [$ok ? 200 : 503, ['status' => $ok ? 'ok' : 'fail', 'db' => 'ok', 'cron_lag_s' => $lag]];
    }
}
