<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Reports from agents: runs (A.5) with their database dumps, logs on request (A.7) and storage inventory (A.8).
 */
final class Reports
{
    public const BACKUP_TYPES = ['full', 'incremental', 'differential', 'mirror'];
    public const TRIGGERS = ['schedule', 'catch_up', 'manual', 'remote'];
    public const PHASES = ['pre_events', 'vss', 'scan', 'copy', 'verify', 'retention', 'post_events'];
    public const FINAL_STATUSES = ['success', 'warning', 'error', 'aborted'];
    public const MAX_LOG_TAIL = 1024 * 1024;

    public function __construct(private readonly Db $db, private readonly Tasks $tasks, private readonly Monitor $monitor)
    {
    }

    /** runs/start. Idempotent by run_id. */
    public function start(string $agentId, Input $in): void
    {
        $runId = $in->uuid('run_id');
        $taskId = $in->uuid('task_id');
        $trigger = $in->enum('trigger', self::TRIGGERS);
        $scheduledFor = $in->time('scheduled_for', true);
        if ($scheduledFor === null && in_array($trigger, ['schedule', 'catch_up'], true)) {
            throw new InvalidInput("scheduled_for is required for trigger $trigger");
        }
        $row = [
            'task_revision' => $in->int('task_revision', true),
            'backup_type' => $in->enum('backup_type', self::BACKUP_TYPES),
            'trigger_type' => $trigger,
            'scheduled_for' => $scheduledFor,
            'started_at' => $in->time('started_at'),
        ];
        $this->db->transaction(function () use ($agentId, $runId, $taskId, $row) {
            $existing = $this->ownRun($agentId, $runId, $taskId);
            if ($existing !== null) {
                return;
            }
            $this->db->exec(
                "INSERT INTO runs (id, agent_id, task_id, task_revision, backup_type, trigger_type, scheduled_for, started_at, status, received_at)
                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'running', ?)",
                [$runId, $agentId, $taskId, $row['task_revision'], $row['backup_type'], $row['trigger_type'],
                 $row['scheduled_for'], $row['started_at'], Time::now()]
            );
            $this->monitor->runStarted($agentId, $taskId, $runId, $row['scheduled_for'], time());
        });
    }

    /** runs/progress: one row per agent with the latest progress of its current run. */
    public function progress(string $agentId, Input $in): void
    {
        $values = [
            $agentId,
            $in->uuid('run_id'),
            $in->enum('phase', self::PHASES),
            $in->number('percent', true, 0, 100),
            $in->int('files_done', true),
            $in->int('files_total', true),
            $in->int('bytes_done', true),
            $in->int('bytes_total', true),
            $in->int('speed_bps', true),
            $in->int('eta_s', true),
            $in->string('current_file', 4096, true),
            Time::now(),
        ];
        $this->db->exec(
            'REPLACE INTO run_progress (agent_id, run_id, phase, percent, files_done, files_total, bytes_done, bytes_total,
                                        speed_bps, eta_s, current_file, updated_at)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
            array_map(fn($v) => is_float($v) ? (string) $v : $v, $values)
        );
    }

    /** runs/finish. Idempotent by run_id: a repeated finish of a finished run changes nothing. */
    public function finish(string $agentId, Input $in): void
    {
        $runId = $in->uuid('run_id');
        $taskId = $in->uuid('task_id');
        $finishedAt = $in->time('finished_at');
        $fields = [
            'status' => $in->enum('status', self::FINAL_STATUSES),
            'source_files' => $in->int('source_files', true),
            'source_bytes' => $in->int('source_bytes', true),
            'files_processed' => $in->int('files_processed', true),
            'bytes_written' => $in->int('bytes_written', true),
            'errors' => $in->int('errors', true),
            'warnings' => $in->int('warnings', true),
            'error_message' => $in->string('error_message', 65535, true),
            'verify' => self::json($in->object('verify', true)),
            'retention' => self::json($in->object('retention', true)),
            'db_dumps' => self::dumps($in),
            'log_tail' => $in->string('log_tail', self::MAX_LOG_TAIL, true),
        ];
        $destinations = [];
        foreach ($in->objects('destinations') as $d) {
            $destinations[$d->uuid('destination_id')] = [
                $d->code('status'),
                $d->string('set_name', 255, true),
                $d->int('bytes', true),
                $d->string('error', 65535, true),
            ];
        }
        $log = self::decodeLog($in, 'log_gz_b64', true);

        $this->db->transaction(function () use ($agentId, $runId, $taskId, $finishedAt, $fields, $destinations, $log) {
            $existing = $this->ownRun($agentId, $runId, $taskId);
            if ($existing !== null && $existing['finished_at'] !== null) {
                return;
            }
            if ($existing === null) {
                // The start report never arrived; keep the run anyway, with the finish time as its start.
                $this->db->exec(
                    "INSERT INTO runs (id, agent_id, task_id, started_at, status, received_at) VALUES (?, ?, ?, ?, 'running', ?)",
                    [$runId, $agentId, $taskId, $finishedAt, Time::now()]
                );
                $startedAt = $finishedAt;
            } else {
                $startedAt = $existing['started_at'];
            }
            $duration = max(0, Time::unix($finishedAt) - Time::unix($startedAt));
            $this->db->exec(
                'UPDATE runs SET finished_at = ?, duration_s = ?, status = ?, source_files = ?, source_bytes = ?, files_processed = ?,
                                 bytes_written = ?, errors = ?, warnings = ?, error_message = ?, verify = ?, retention = ?, db_dumps = ?,
                                 log_tail = ?
                 WHERE id = ?',
                [$finishedAt, $existing === null ? null : $duration, ...array_values($fields), $runId]
            );
            foreach ($destinations as $destinationId => $d) {
                $this->db->exec(
                    'REPLACE INTO run_destinations (run_id, destination_id, status, set_name, bytes, error) VALUES (?, ?, ?, ?, ?, ?)',
                    [$runId, $destinationId, ...$d]
                );
            }
            if ($log !== null) {
                $this->storeLog($runId, $log, 'finish');
            }
            $this->db->exec('DELETE FROM run_progress WHERE agent_id = ? AND run_id = ?', [$agentId, $runId]);
            $this->monitor->runFinished($runId, time());
            $this->tasks->refreshRunCache($agentId, $taskId);
        });
    }

    /** runs/{run_id}/log: the full log sent in answer to log.fetch. */
    public function log(string $agentId, string $runId, Input $in): void
    {
        if (!preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $runId)) {
            throw new InvalidInput('run id in the path must be a UUID');
        }
        $runId = strtolower($runId);
        $log = self::decodeLog($in, 'log_gz_b64', false);
        $owner = $this->db->value('SELECT agent_id FROM runs WHERE id = ?', [$runId]);
        if ($owner !== $agentId) {
            throw new InvalidInput("run $runId is not known for this agent");
        }
        $this->storeLog($runId, $log, 'fetch');
    }

    /** inventory: free space history and the sets that are in each destination now. */
    public function inventory(string $agentId, Input $in): void
    {
        $destinations = [];
        foreach ($in->objects('destinations') as $d) {
            $sets = [];
            foreach ($d->objects('sets') as $s) {
                $sets[] = [
                    $s->string('name'),
                    $s->code('type', true),
                    $s->time('time', true),
                    $s->int('bytes', true),
                    $s->time('verified_at', true),
                    $s->bool('verify_ok', true),
                ];
            }
            $destinations[] = [
                'task_id' => $d->uuid('task_id'),
                'destination_id' => $d->uuid('destination_id'),
                'kind' => $d->code('kind'),
                'path' => $d->string('path', 1024, true),
                'total_bytes' => $d->int('total_bytes', true),
                'free_bytes' => $d->int('free_bytes', true),
                'sets' => $sets,
            ];
        }
        $now = Time::now();
        $this->db->transaction(function () use ($agentId, $destinations, $now) {
            foreach ($destinations as $d) {
                $this->db->exec(
                    'INSERT INTO storage_space (agent_id, task_id, destination_id, kind, path, total_bytes, free_bytes, recorded_at)
                     VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
                    [$agentId, $d['task_id'], $d['destination_id'], $d['kind'], $d['path'], $d['total_bytes'], $d['free_bytes'], $now]
                );
                $this->db->exec(
                    'DELETE FROM storage_sets WHERE agent_id = ? AND task_id = ? AND destination_id = ?',
                    [$agentId, $d['task_id'], $d['destination_id']]
                );
                foreach ($d['sets'] as $set) {
                    $this->db->exec(
                        'INSERT INTO storage_sets (agent_id, task_id, destination_id, name, set_type, set_time, bytes, verified_at, verify_ok, updated_at)
                         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
                        [$agentId, $d['task_id'], $d['destination_id'], $set[0], $set[1], $set[2], $set[3], $set[4],
                         $set[5] === null ? null : (int) $set[5], $now]
                    );
                }
            }
        });
    }

    /** Returns the run if it exists and belongs to this agent and task, null if it does not exist. */
    private function ownRun(string $agentId, string $runId, string $taskId): ?array
    {
        $run = $this->db->one('SELECT agent_id, task_id, started_at, finished_at FROM runs WHERE id = ? FOR UPDATE', [$runId]);
        if ($run !== null && ($run['agent_id'] !== $agentId || $run['task_id'] !== $taskId)) {
            throw new InvalidInput("run $runId belongs to another agent or task");
        }
        return $run;
    }

    private function storeLog(string $runId, string $gz, string $source): void
    {
        $this->db->exec(
            'REPLACE INTO run_logs (run_id, log_gz, source, received_at) VALUES (?, ?, ?, ?)',
            [$runId, $gz, $source, Time::now()]
        );
    }

    /** Decodes a *_gz_b64 field (A.1) and checks that it is gzip. */
    private static function decodeLog(Input $in, string $key, bool $nullable): ?string
    {
        $base64 = $in->string($key, PHP_INT_MAX, $nullable);
        if ($base64 === null) {
            return null;
        }
        $gz = base64_decode($base64, true);
        if ($gz === false || !str_starts_with($gz, "\x1f\x8b")) {
            throw new InvalidInput("$key must be gzip data in base64");
        }
        return $gz;
    }

    /**
     * "databases" of runs/finish (A.5, client stage 2b): one entry per dumped database. Optional: missing for tasks
     * without database servers. Unknown fields of an entry are kept.
     */
    private static function dumps(Input $in): ?string
    {
        if (!$in->has('databases') || $in->raw('databases') === null) {
            return null;
        }
        $dumps = [];
        foreach ($in->objects('databases') as $d) {
            $dumps[] = [
                'engine' => $d->code('engine', true),
                'name' => $d->string('name', 255),
                'bytes' => $d->int('bytes', true),
                'ok' => $d->bool('ok', true),
                'error' => $d->string('error', 65535, true),
            ] + $d->toArray();
        }
        return json_encode($dumps, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    }

    private static function json(?Input $value): ?string
    {
        return $value === null ? null : json_encode($value->toArray(), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    }
}
