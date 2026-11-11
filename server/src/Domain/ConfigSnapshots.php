<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Configuration snapshots (A.6): the full list of tasks, credentials without secrets and agent settings.
 */
final class ConfigSnapshots
{
    private const JSON_FLAGS = JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES;
    /** Credential fields kept on the server; anything else an agent might send is dropped. */
    private const CREDENTIAL_FIELDS = ['id', 'kind', 'user', 'domain', 'has_secret'];

    public function __construct(private readonly Db $db, private readonly Tasks $tasks)
    {
    }

    /**
     * @param string $body the raw request body: task objects are stored exactly as sent, unknown fields
     *                     and empty objects included (A.9)
     */
    public function apply(string $agentId, Input $in, string $body): void
    {
        $raw = json_decode($body, false, 64, JSON_BIGINT_AS_STRING);
        $revision = $in->int('config_revision');
        $settings = $in->object('settings', true);
        $settingsJson = null;
        $allowRemoteEdit = null;
        $passwordSet = null;
        if ($settings !== null) {
            $allowRemoteEdit = $settings->bool('allow_remote_edit', true);
            $passwordSet = $settings->has('program_password_hash');
            $stored = is_object($raw->settings) ? clone $raw->settings : new \stdClass();
            // Only the fact that a program password is set is needed; the hash itself stays on the agent.
            unset($stored->program_password_hash);
            $settingsJson = json_encode($stored, self::JSON_FLAGS);
        }

        $credentials = [];
        foreach ($in->objects('credentials') as $c) {
            $c->uuid('id');
            $c->code('kind');
            $c->string('user', 255, true);
            $c->string('domain', 255, true);
            $c->bool('has_secret', true);
            $credentials[] = array_intersect_key($c->toArray(), array_flip(self::CREDENTIAL_FIELDS));
        }

        $tasks = [];
        foreach ($in->objects('tasks') as $i => $task) {
            $parsed = self::task($task, $raw->tasks[$i]);
            if (isset($tasks[$parsed['id']])) {
                throw new InvalidInput("tasks[$i].id {$parsed['id']} is listed twice");
            }
            $tasks[$parsed['id']] = $parsed;
        }
        $overwritten = [];
        foreach ($in->objects('overwritten') as $i => $task) {
            $overwritten[] = self::task($task, $raw->overwritten[$i]);
        }
        $deletedIds = $in->uuids('deleted_task_ids');
        foreach ($in->objects('changed') as $change) {
            $change->uuid('task_id');
        }

        $now = Time::now();
        $this->db->transaction(function () use ($agentId, $revision, $settingsJson, $allowRemoteEdit, $passwordSet, $credentials, $tasks, $overwritten, $deletedIds, $now) {
            $known = [];
            foreach ($this->db->all('SELECT id, revision, deleted FROM tasks WHERE agent_id = ? FOR UPDATE', [$agentId]) as $row) {
                $known[$row['id']] = $row;
            }
            foreach ($tasks as $id => $task) {
                if (isset($known[$id])) {
                    $this->db->exec(
                        'UPDATE tasks SET revision = ?, name = ?, enabled = ?, data = ?, updated_at = ?, updated_by = ?, deleted = 0, deleted_at = NULL
                         WHERE agent_id = ? AND id = ?',
                        [$task['revision'], $task['name'], (int) $task['enabled'], $task['data'], $task['updated_at'], $task['updated_by'], $agentId, $id]
                    );
                } else {
                    $this->db->exec(
                        'INSERT INTO tasks (agent_id, id, revision, name, enabled, data, updated_at, updated_by, first_seen_at)
                         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)',
                        [$agentId, $id, $task['revision'], $task['name'], (int) $task['enabled'], $task['data'], $task['updated_at'], $task['updated_by'], $now]
                    );
                    $this->tasks->refreshRunCache($agentId, $id);
                }
                $this->addVersion($agentId, $task, false, $now);
            }
            // The snapshot is complete: a task the agent no longer lists has been deleted there.
            foreach ($known as $id => $row) {
                if (!isset($tasks[$id]) && !$row['deleted']) {
                    $deletedIds[] = $id;
                }
            }
            foreach (array_unique($deletedIds) as $id) {
                if (!isset($tasks[$id])) {
                    $this->db->exec(
                        'UPDATE tasks SET deleted = 1, deleted_at = ? WHERE agent_id = ? AND id = ? AND deleted = 0',
                        [$now, $agentId, $id]
                    );
                }
            }
            foreach ($overwritten as $task) {
                $this->addVersion($agentId, $task, true, $now);
            }
            $this->db->exec(
                'UPDATE agents SET config_revision = ?, config_received_at = ?, settings = COALESCE(?, settings),
                                   allow_remote_edit = COALESCE(?, allow_remote_edit), program_password_set = COALESCE(?, program_password_set),
                                   credentials = ?
                 WHERE id = ?',
                [
                    $revision, $now, $settingsJson,
                    $allowRemoteEdit === null ? null : (int) $allowRemoteEdit,
                    $passwordSet === null ? null : (int) $passwordSet,
                    json_encode($credentials, self::JSON_FLAGS), $agentId,
                ]
            );
        });
    }

    /** @return array{id: string, revision: int, name: string, enabled: bool, updated_at: ?string, updated_by: ?string, data: string} */
    private static function task(Input $task, object $raw): array
    {
        return [
            'id' => $task->uuid('id'),
            'revision' => $task->int('revision'),
            'name' => $task->string('name'),
            'enabled' => $task->bool('enabled'),
            'updated_at' => $task->time('updated_at', true),
            'updated_by' => $task->string('updated_by', 255, true),
            'data' => json_encode($raw, self::JSON_FLAGS),
        ];
    }

    private function addVersion(string $agentId, array $task, bool $overwritten, string $now): void
    {
        $source = str_starts_with((string) $task['updated_by'], 'server:') ? 'server' : 'local';
        $this->db->exec(
            'INSERT INTO task_versions (agent_id, task_id, revision, data, updated_at, updated_by, source, overwritten, received_at)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
             ON DUPLICATE KEY UPDATE overwritten = GREATEST(overwritten, ?)',
            [$agentId, $task['id'], $task['revision'], $task['data'], $task['updated_at'], $task['updated_by'], $source,
             (int) $overwritten, $now, (int) $overwritten]
        );
    }
}
