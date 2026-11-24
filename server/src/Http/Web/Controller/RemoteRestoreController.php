<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\Domain\Agents;
use Custos\Domain\RemoteRestore;
use Custos\Http\Response;
use Custos\Http\Web\Controller;
use Custos\Infra\Time;

final class RemoteRestoreController extends Controller
{
    public function show(string $agentId, string $taskId): Response
    {
        [$agent, $task] = $this->load($agentId, $taskId);
        if ($task === null) {
            return $this->notFound();
        }
        $list = $this->latest($agent['id'], $task['id'], RemoteRestore::LIST);
        $definition = json_decode($task['data'], true) ?: [];
        $destinations = [];
        foreach ($definition['destinations'] ?? [] as $index => $destination) {
            if (isset($destination['id'])) {
                $destinations[strtolower($destination['id'])] = $index + 1;
            }
        }
        return $this->render('tasks/backup-restore', [
            'section' => 'agents',
            'live' => 5,
            'agent' => $agent,
            'task' => $task,
            'list' => $list,
            'listData' => self::data($list),
            'destinations' => $destinations,
        ] + $this->currentJob($agent['id'], $task['id']), $this->t('restore.title'));
    }

    public function command(string $agentId, string $taskId, string $action): Response
    {
        return $this->app->db()->transaction(fn() => $this->queueCommand($agentId, $taskId, $action));
    }

    private function queueCommand(string $agentId, string $taskId, string $action): Response
    {
        // Serialize the active-job check and enqueue for this agent across concurrent web requests.
        $agent = $this->app->db()->one('SELECT * FROM agents WHERE id = ? FOR UPDATE', [strtolower($agentId)]);
        [, $task] = $this->loadTask($agent, $taskId);
        if ($task === null) {
            return $this->notFound();
        }
        $agentId = $agent['id'];
        $taskId = $task['id'];
        $back = '/agents/' . $agentId . '/tasks/' . $taskId . '/backup-restore';
        $kind = 'restore.' . $action;
        $payload = ['task_id' => $taskId];
        if ($kind === RemoteRestore::START) {
            $list = $this->latest($agentId, $taskId, RemoteRestore::LIST);
            $set = $list !== null && $list['id'] === $this->post('list_id') ? (self::data($list)['sets'][$this->post('set')] ?? null) : null;
            if ($set === null) {
                return $this->refuse($back, 'restore.invalid_set');
            }
            if ($this->currentJob($agentId, $taskId)['active']) {
                return $this->refuse($back, 'restore.active');
            }
            $payload += ['set_run_id' => $set['set_run_id'], 'destination_id' => $set['destination_id']];
        } elseif ($kind === RemoteRestore::STATUS || $kind === RemoteRestore::STOP) {
            $current = $this->currentJob($agentId, $taskId);
            $start = $current['start'];
            if (!$current['active'] || $start['id'] !== $this->post('job_id')) {
                return $this->refuse($back, 'restore.invalid_job');
            }
            if ($start['status'] === 'queued') {
                if ($kind !== RemoteRestore::STOP || !$this->app->commands()->cancel($agentId, $start['id'])) {
                    return $this->refuse($back, 'restore.refresh');
                }
                $this->audit('command.cancel', 'agent', $agentId, ['name' => Agents::name($agent), 'command' => RemoteRestore::START, 'task' => $task['name']]);
                return $this->redirect($back);
            }
            if ($start['status'] !== 'done' || (self::data($start)['job']['job_id'] ?? null) !== $start['id']) {
                return $this->refuse($back, 'restore.refresh');
            }
            $payload['job_id'] = $start['id'];
        }

        $this->app->commands()->queue($agentId, $kind, $payload, $this->user());
        $this->audit('command', 'agent', $agentId, ['name' => Agents::name($agent), 'command' => $kind, 'task' => $task['name']] + $payload);
        return $this->redirect($back);
    }

    private function load(string $agentId, string $taskId): array
    {
        return $this->loadTask($this->app->agents()->find($agentId), $taskId);
    }

    private function loadTask(?array $agent, string $taskId): array
    {
        $task = $agent !== null && Agents::canRemoteRestore($agent) ? $this->app->tasks()->find($agent['id'], strtolower($taskId)) : null;
        return [$agent, $task !== null && empty($task['deleted']) ? $task : null];
    }

    private function latest(string $agentId, string $taskId, string $kind): ?array
    {
        return $this->app->db()->one('SELECT * FROM commands WHERE agent_id = ? AND task_id = ? AND kind = ?
            AND restore_obsolete = 0 ORDER BY queue_sequence DESC LIMIT 1',
            [$agentId, $taskId, $kind]);
    }

    private function currentJob(string $agentId, string $taskId): array
    {
        $start = $this->latest($agentId, $taskId, RemoteRestore::START);
        $job = self::data($start)['job'] ?? null;
        $lastRequest = null;
        if ($job !== null) {
            $args = [$agentId, $taskId, $start['id']];
            $where = " WHERE agent_id = ? AND task_id = ? AND restore_obsolete = 0 AND kind IN ('restore.status', 'restore.stop')
                       AND JSON_UNQUOTE(JSON_EXTRACT(payload, '$.job_id')) = ?";
            $db = $this->app->db();
            $lastRequest = $db->one('SELECT * FROM commands' . $where . ' ORDER BY queue_sequence DESC LIMIT 1', $args);
            // A later network reply reporting 'running' cannot replace an already reported terminal state.
            $confirmed = $db->one('SELECT * FROM commands' . $where . " AND status = 'done'
                ORDER BY (JSON_UNQUOTE(JSON_EXTRACT(result_data, '$.job.state')) = 'running'), queue_sequence DESC LIMIT 1", $args);
            $job = self::data($confirmed)['job'] ?? $job;
        }
        $missing = $lastRequest !== null && $lastRequest['kind'] === RemoteRestore::STATUS
            && (self::data($lastRequest)['error'] ?? null) === 'job_missing';
        $waiting = $start !== null && in_array($start['status'], ['queued', 'delivered'], true) && $start['expires_at'] > Time::now();
        $active = !$missing && ($waiting || ($job['state'] ?? null) === 'running');
        return ['start' => $start, 'job' => $job, 'lastRequest' => $lastRequest, 'active' => $active, 'missing' => $missing];
    }

    private function refuse(string $back, string $key): Response
    {
        $this->session->flash('error', $this->t($key));
        return $this->redirect($back);
    }

    private static function data(?array $command): array
    {
        return json_decode($command['result_data'] ?? 'null', true) ?? [];
    }
}
