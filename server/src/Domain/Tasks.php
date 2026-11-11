<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;

/**
 * Tasks as the agents report them in configuration snapshots (A.6, A.9).
 */
final class Tasks
{
    public function __construct(private readonly Db $db)
    {
    }

    public function forAgent(string $agentId, bool $withDeleted = false): array
    {
        return $this->db->all(
            'SELECT * FROM tasks WHERE agent_id = ?' . ($withDeleted ? '' : ' AND deleted = 0') . ' ORDER BY deleted, name',
            [$agentId]
        );
    }

    public function find(string $agentId, string $taskId): ?array
    {
        return $this->db->one('SELECT * FROM tasks WHERE agent_id = ? AND id = ?', [$agentId, $taskId]);
    }

    /**
     * Recomputes the cached run data of a task: the last run, the last successful run, the latest result that was not
     * aborted (for warning/ok, 6.6) and whether the latest successful run is an anomaly (6.5).
     */
    public function refreshRunCache(string $agentId, string $taskId): void
    {
        $last = $this->db->one(
            'SELECT id, status, started_at FROM runs
             WHERE agent_id = ? AND task_id = ? AND finished_at IS NOT NULL
             ORDER BY started_at DESC LIMIT 1',
            [$agentId, $taskId]
        );
        $result = $this->db->one(
            "SELECT id, status FROM runs
             WHERE agent_id = ? AND task_id = ? AND finished_at IS NOT NULL AND status <> 'aborted'
             ORDER BY started_at DESC LIMIT 1",
            [$agentId, $taskId]
        );
        $success = $this->db->one(
            "SELECT started_at, anomaly FROM runs WHERE agent_id = ? AND task_id = ? AND status IN ('success', 'warning')
             ORDER BY started_at DESC LIMIT 1",
            [$agentId, $taskId]
        );
        $this->db->exec(
            'UPDATE tasks SET last_run_id = ?, last_run_status = ?, last_run_at = ?, last_success_at = ?,
                              last_result = ?, last_result_run_id = ?, anomaly = ?
             WHERE agent_id = ? AND id = ?',
            [
                $last['id'] ?? null, $last['status'] ?? null, $last['started_at'] ?? null, $success['started_at'] ?? null,
                $result['status'] ?? null, $result['id'] ?? null, (int) (($success['anomaly'] ?? null) !== null),
                $agentId, $taskId,
            ]
        );
    }
}
