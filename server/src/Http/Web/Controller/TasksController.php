<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Time;

/** The task page (7.4, the parts of stage 2): description, status, problems, expected slots and recent runs. */
final class TasksController extends Controller
{
    public const SLOT_DAYS = 30;

    public function show(string $agentId, string $taskId): Response
    {
        $agentId = strtolower($agentId);
        $taskId = strtolower($taskId);
        $board = $this->app->board($agentId);
        $agent = $board->agents[$agentId] ?? null;
        $task = $agent === null ? null : $board->task($agentId, $taskId);
        $db = $this->app->db();
        if ($task === null) {
            // A deleted task is still shown, with its history.
            $row = $agent === null ? null : $this->app->tasks()->find($agentId, $taskId);
            if ($row === null) {
                return $this->notFound();
            }
            $task = $row + ['def' => json_decode($row['data'], true) ?: [], 'status' => 'disabled', 'flags' => [], 'next_at' => null,
                            'problems' => [], 'offline_slots' => [], 'queued' => false, 'watched' => false];
        }
        return $this->render('tasks/show', [
            'section' => 'agents',
            'agent' => $agent,
            'task' => $task,
            'problems' => $db->all(
                'SELECT * FROM problems WHERE agent_id = ? AND task_id = ? ORDER BY closed_at IS NULL DESC, opened_at DESC LIMIT 20',
                [$agentId, $taskId]
            ),
            'slots' => $db->all(
                'SELECT * FROM expected_slots WHERE agent_id = ? AND task_id = ? AND slot_at >= ? ORDER BY slot_at DESC LIMIT 200',
                [$agentId, $taskId, Time::db(time() - self::SLOT_DAYS * 86400)]
            ),
            'runs' => $db->all(
                'SELECT r.*, ? AS task_name FROM runs r WHERE r.agent_id = ? AND r.task_id = ? ORDER BY r.started_at DESC LIMIT 20',
                [$task['name'], $agentId, $taskId]
            ),
            'progress' => $agent['running_task'] === $taskId
                ? $db->one('SELECT * FROM run_progress WHERE agent_id = ? AND run_id = ?', [$agentId, (string) $agent['running_run']])
                : null,
        ], $task['name'] . ' · ' . Agents::name($agent));
    }
}
