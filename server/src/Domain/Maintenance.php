<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Daily cleanup by the retention periods of 7.8: run history, presence, problems, notices and the audit log are kept
 * for history_months months, full run logs for log_days days.
 */
final class Maintenance
{
    private const BATCH = 5000;

    public function __construct(private readonly Db $db, private readonly Settings $settings)
    {
    }

    /** @return array<string, int> deleted rows per table */
    public function cleanup(int $now): array
    {
        $months = max(1, $this->settings->int('history_months'));
        $history = Time::db((new \DateTimeImmutable('@' . $now))->modify("-$months months")->getTimestamp());
        $logs = Time::db($now - max(1, $this->settings->int('log_days')) * 86400);

        $deleted = [];
        // Full logs go first and by the run's start, so that a log is never kept longer than its run.
        $deleted['run_logs'] = $this->byIds(
            'SELECT l.run_id FROM run_logs l JOIN runs r ON r.id = l.run_id WHERE r.started_at < ? LIMIT ' . self::BATCH,
            [$logs],
            'DELETE FROM run_logs WHERE run_id IN (%s)'
        );
        // Runs take their destinations and logs with them (foreign keys).
        $deleted['runs'] = $this->batched('DELETE FROM runs WHERE started_at < ? AND finished_at IS NOT NULL', [$history]);
        $deleted['runs_unfinished'] = $this->batched('DELETE FROM runs WHERE started_at < ? AND finished_at IS NULL', [$history]);
        $deleted['agent_presence'] = $this->batched('DELETE FROM agent_presence WHERE to_at < ?', [$history]);
        $deleted['expected_slots'] = $this->batched('DELETE FROM expected_slots WHERE slot_at < ? AND result <> \'pending\'', [$history]);
        $deleted['problems'] = $this->batched('DELETE FROM problems WHERE closed_at IS NOT NULL AND closed_at < ?', [$history]);
        $deleted['notices'] = $this->batched('DELETE FROM notices WHERE created_at < ?', [$history]);
        $deleted['notifications'] = $this->batched('DELETE FROM notifications WHERE created_at < ?', [$history]);
        $deleted['audit_log'] = $this->batched('DELETE FROM audit_log WHERE created_at < ?', [$history]);
        $deleted['commands'] = $this->batched(
            "DELETE FROM commands WHERE created_at < ? AND status IN ('done', 'failed', 'rejected', 'expired')",
            [$history]
        );
        $deleted['storage_space'] = $this->batched('DELETE FROM storage_space WHERE recorded_at < ?', [$history]);
        $deleted['task_versions'] = $this->batched(
            'DELETE v FROM task_versions v JOIN tasks t ON t.agent_id = v.agent_id AND t.id = v.task_id
             WHERE t.deleted = 1 AND t.deleted_at < ?',
            [$history],
            false
        );
        $deleted['tasks'] = $this->batched('DELETE FROM tasks WHERE deleted = 1 AND deleted_at < ?', [$history]);
        return array_filter($deleted);
    }

    private function batched(string $sql, array $params, bool $limit = true): int
    {
        $total = 0;
        do {
            $count = $this->db->exec($sql . ($limit ? ' LIMIT ' . self::BATCH : ''), $params);
            $total += $count;
        } while ($limit && $count === self::BATCH);
        return $total;
    }

    private function byIds(string $select, array $params, string $delete): int
    {
        $total = 0;
        while (($ids = $this->db->column($select, $params)) !== []) {
            $total += $this->db->exec(sprintf($delete, implode(',', array_fill(0, count($ids), '?'))), $ids);
            if (count($ids) < self::BATCH) {
                break;
            }
        }
        return $total;
    }
}
