<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Crypto;
use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Changes of tasks and agent settings from the site (7.3–7.5, A.4): what the editor starts from, which revision the
 * change is based on, and the task.upsert / settings.update commands with passwords encrypted for the agent.
 */
final class TaskEdits
{
    public function __construct(private readonly Db $db, private readonly Tasks $tasks, private readonly Commands $commands)
    {
    }

    /** Accounts of the machine by id, as its last configuration listed them (without secrets). */
    public static function credentials(array $agent): array
    {
        $result = [];
        foreach ((array) json_decode((string) ($agent['credentials'] ?? ''), true) as $c) {
            if (is_array($c) && is_string($c['id'] ?? null)) {
                $result[$c['id']] = $c;
            }
        }
        return $result;
    }

    /** "Allow the server to change tasks" is off on the machine: edits would be rejected (A.4). */
    public static function remoteEditOff(array $agent): bool
    {
        return $agent['allow_remote_edit'] !== null && !(int) $agent['allow_remote_edit'];
    }

    /**
     * The latest edit of the task from the site that the server has not yet seen in a configuration snapshot:
     * waiting for delivery, delivered, or done but not yet reported back. Null when there is none.
     *
     * The agent gives an applied edit a revision above base_revision, so a done edit is reported once the stored task
     * has a newer revision than the edit's base. The order of the result and the snapshot does not matter.
     */
    public function pending(array $agent, string $taskId): ?array
    {
        $row = $this->db->one(
            "SELECT * FROM commands WHERE agent_id = ? AND task_id = ? AND kind = 'task.upsert'
             AND status IN ('queued', 'delivered', 'done') ORDER BY created_at DESC LIMIT 1",
            [$agent['id'], $taskId]
        );
        if ($row === null) {
            return null;
        }
        $row['data'] = self::decode($row['payload']);
        if ($row['status'] === 'done') {
            $stored = $this->tasks->find($agent['id'], $taskId);
            return $stored === null || (int) $stored['revision'] <= (int) ($row['data']['base_revision'] ?? 0) ? $row : null;
        }
        return Time::unix($row['expires_at']) > time() ? $row : null;
    }

    /**
     * Where the editor starts: the edit still on its way if there is one, else the task as the agent reported it.
     *
     * @return array{task: array, pending: ?array, stored: ?array}|null null when the task is not known
     */
    public function current(array $agent, string $taskId): ?array
    {
        $stored = $this->tasks->find($agent['id'], $taskId);
        $pending = $this->pending($agent, $taskId);
        if ($stored === null && $pending === null) {
            return null;
        }
        $task = $pending !== null && is_array($pending['data']['task'] ?? null)
            ? $pending['data']['task']
            : self::decode((string) $stored['data']);
        return ['task' => $task, 'pending' => $pending, 'stored' => $stored];
    }

    /**
     * base_revision of the next edit (A.4). An edit that is still queued is replaced, so the next one has the same base.
     * An edit the agent already has will become revision max(stored, base) + 1 there; the next edit is based on that,
     * so that the agent does not report our own previous edit as a conflict.
     */
    public function baseRevision(array $agent, string $taskId): int
    {
        $stored = $this->tasks->find($agent['id'], $taskId);
        $revision = $stored === null || $stored['deleted'] ? (int) ($stored['revision'] ?? 0) : (int) $stored['revision'];
        $pending = $this->pending($agent, $taskId);
        if ($pending === null) {
            return $revision;
        }
        $base = (int) ($pending['data']['base_revision'] ?? 0);
        return $pending['status'] === 'queued' ? $base : max($revision, $base) + 1;
    }

    /**
     * Queues task.upsert. $task is a full task object; $secrets are new accounts and new passwords of existing ones:
     * id => {kind, user, domain, password}. Passwords are encrypted for this agent here and never stored in plain text.
     *
     * @param array{id: int, login: string} $user
     */
    public function upsert(array $agent, array $task, int $baseRevision, array $secrets, array $user): string
    {
        $key = Crypto::loadPublicKey($agent['public_key']) ?? throw new \RuntimeException('The agent key cannot be read.');
        $task['revision'] = $baseRevision;
        $task['updated_at'] = Time::isoNow();
        $task['updated_by'] = 'server:' . $user['login'];
        $encrypted = new \stdClass();
        foreach ($secrets as $id => $secret) {
            $encrypted->$id = [
                'kind' => $secret['kind'],
                'user' => $secret['user'],
                'domain' => $secret['domain'],
                'secret_enc' => Crypto::encryptSecret($secret['password'], $key),
            ];
        }
        return $this->commands->queue($agent['id'], 'task.upsert', [
            'task' => self::object($task),
            'base_revision' => $baseRevision,
            'secrets' => $encrypted,
        ], $user);
    }

    /**
     * Saves an edit made in the editor: new accounts and replaced passwords go along as secrets.
     *
     * @param array{id: int, login: string} $user
     */
    public function save(array $agent, TaskForm $form, int $baseRevision, array $user): string
    {
        $credentials = self::credentials($agent);
        $secrets = [];
        foreach ($form->newCredentials as $id => $c) {
            $secrets[$id] = $c;
        }
        foreach ($form->replacedSecrets as $id => $password) {
            $c = $credentials[$id];
            $secrets[$id] = ['kind' => (string) $c['kind'], 'user' => (string) ($c['user'] ?? ''), 'domain' => $c['domain'] ?? null, 'password' => $password];
        }
        return $this->upsert($agent, $form->task(), $baseRevision, $secrets, $user);
    }

    /**
     * "Return this version" (7.4): the old settings become a new revision. Accounts the version refers to must still
     * exist on the machine, since passwords cannot be sent again from history.
     *
     * @return array{command: ?string, missing: list<string>}
     */
    public function restore(array $agent, string $taskId, int $revision, array $user): array
    {
        $version = $this->db->value(
            'SELECT data FROM task_versions WHERE agent_id = ? AND task_id = ? AND revision = ?',
            [$agent['id'], $taskId, $revision]
        );
        $task = $version === null ? null : self::decode((string) $version);
        if ($task === null) {
            throw new \InvalidArgumentException('task_versions.error.unknown');
        }
        $credentials = self::credentials($agent);
        $missing = array_values(array_filter(TaskForm::credentialRefs($task), fn($id) => !isset($credentials[$id])));
        if ($missing !== []) {
            return ['command' => null, 'missing' => $missing];
        }
        return ['command' => $this->upsert($agent, $task, $this->baseRevision($agent, $taskId), [], $user), 'missing' => []];
    }

    /**
     * settings.update (A.4): only the settings that changed. The program password goes as its hash (A.2).
     *
     * @param array{id: int, login: string} $user
     */
    public function updateSettings(array $agent, array $changes, array $user): string
    {
        return $this->commands->queue($agent['id'], 'settings.update', ['settings' => self::object($changes)], $user);
    }

    /** Versions of a task, newest first. */
    public function versions(string $agentId, string $taskId): array
    {
        return $this->db->all(
            'SELECT revision, updated_at, updated_by, source, overwritten, received_at FROM task_versions
             WHERE agent_id = ? AND task_id = ? ORDER BY revision DESC',
            [$agentId, $taskId]
        );
    }

    public function version(string $agentId, string $taskId, int $revision): ?array
    {
        $row = $this->db->one(
            'SELECT * FROM task_versions WHERE agent_id = ? AND task_id = ? AND revision = ?',
            [$agentId, $taskId, $revision]
        );
        if ($row === null) {
            return null;
        }
        $row['task'] = self::decode($row['data']);
        $previous = $this->db->one(
            'SELECT revision, data FROM task_versions WHERE agent_id = ? AND task_id = ? AND revision < ? ORDER BY revision DESC LIMIT 1',
            [$agentId, $taskId, $revision]
        );
        $row['previous_revision'] = $previous === null ? null : (int) $previous['revision'];
        $row['previous'] = $previous === null ? null : self::decode($previous['data']);
        return $row;
    }

    /** How an empty JSON object looks after decode(): PHP arrays cannot tell {} from []. */
    public const EMPTY_OBJECT = ['{}' => true];

    /**
     * Decodes stored JSON into arrays, keeping empty objects apart from empty lists (as EMPTY_OBJECT), so that a field
     * this server does not know goes back to the agent exactly as it came (A.9).
     */
    public static function decode(string $json): array
    {
        $data = json_decode($json, false, 64, JSON_BIGINT_AS_STRING);
        $result = self::toArray($data);
        return is_array($result) ? $result : [];
    }

    private static function toArray(mixed $value): mixed
    {
        if ($value instanceof \stdClass) {
            $vars = get_object_vars($value);
            if ($vars === []) {
                return self::EMPTY_OBJECT;
            }
            return array_map(self::toArray(...), $vars);
        }
        if (is_array($value)) {
            return array_map(self::toArray(...), $value);
        }
        return $value;
    }

    /**
     * Arrays to JSON objects where A.9 expects objects, so that an empty "gfs" or "filters" is sent as {} and not [].
     */
    public static function object(array $data): object
    {
        return json_decode(json_encode(self::objectify($data), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES), false);
    }

    private static function objectify(array $data): array|object
    {
        if ($data === self::EMPTY_OBJECT) {
            return new \stdClass();
        }
        foreach ($data as $key => $value) {
            if (is_array($value)) {
                $data[$key] = self::objectify($value);
            }
        }
        return $data === [] || array_is_list($data) ? $data : (object) $data;
    }
}
