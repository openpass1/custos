<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Crypto;
use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Command queue (A.4). Commands are delivered once, in the heartbeat response, and the agent reports the result.
 */
final class Commands
{
    public const RESULT_STATUSES = ['done', 'failed', 'rejected'];
    /** States after which nothing happens to a command any more. */
    public const FINAL = ['done', 'failed', 'rejected', 'expired', 'cancelled'];

    /** Commands the agent refuses when "allow the server to change tasks" is off on the machine (A.4). */
    public const EDITS = ['task.upsert', 'task.delete', 'task.enable', 'task.disable', 'settings.update'];

    private const HOUR = 3600;
    private const DAY = 86400;

    /** How long a command waits for its agent (section 8): run, stop and pause an hour, changes a week, updates 3 days. */
    public const TTL = [
        'task.run' => self::HOUR,
        'task.stop' => self::HOUR,
        'task.pause' => self::HOUR,
        'task.resume' => self::HOUR,
        'task.enable' => 7 * self::DAY,
        'task.disable' => 7 * self::DAY,
        'task.upsert' => 7 * self::DAY,
        'task.delete' => 7 * self::DAY,
        'settings.update' => 7 * self::DAY,
        'log.fetch' => self::DAY,
        'inventory.refresh' => self::DAY,
        'update.install' => 3 * self::DAY,
        'sevenzip.update' => 3 * self::DAY,
        RemoteRestore::LIST => 600,
        RemoteRestore::START => 600,
        RemoteRestore::STATUS => 600,
        RemoteRestore::STOP => 600,
    ];

    /** Commands that only flip something on the agent: a second identical one while the first waits is pointless. */
    private const IDEMPOTENT = ['task.run', 'task.stop', 'task.pause', 'task.resume', 'task.enable', 'task.disable',
                                'task.delete', 'log.fetch', 'inventory.refresh', ...RemoteRestore::COMMANDS];

    private const JSON_FLAGS = JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES;

    public function __construct(private readonly Db $db, private readonly Notices $notices)
    {
    }

    /** Queues a command and returns its id. */
    public function create(string $agentId, string $kind, object|array $payload, int $ttlSeconds, ?int $userId = null, ?string $login = null): string
    {
        $id = Crypto::uuid4();
        $payload = (object) $payload;
        $this->db->exec(
            'INSERT INTO commands (id, agent_id, kind, task_id, payload, created_by, created_by_login, created_at, expires_at)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)',
            [$id, $agentId, $kind, self::taskIdOf($kind, $payload), json_encode($payload, self::JSON_FLAGS),
             $userId, $login, Time::now(), Time::db(time() + $ttlSeconds)]
        );
        return $id;
    }

    /**
     * Queues a command from the site with the lifetime of its kind and returns its id.
     *
     * - A repeated simple command (run, stop, enable…) for the same task that has not been delivered yet is not
     *   queued twice: the waiting one is returned.
     * - A new task.upsert replaces an undelivered task.upsert of the same task: the agent gets only the latest edit, and
     *   passwords entered with the earlier one go along unless entered again.
     * - A new update.install or sevenzip.update replaces an undelivered one of the same kind.
     *
     * @param array{id: int, login: string}|null $user
     */
    public function queue(string $agentId, string $kind, object|array $payload, ?array $user): string
    {
        if (!isset(self::TTL[$kind])) {
            throw new \InvalidArgumentException("Unknown command $kind");
        }
        $payload = json_decode(json_encode((object) $payload, self::JSON_FLAGS), false);
        $taskId = self::taskIdOf($kind, $payload);
        return $this->db->transaction(function () use ($agentId, $kind, $payload, $taskId, $user) {
            $waiting = $this->db->all(
                "SELECT id, payload FROM commands
                 WHERE agent_id = ? AND kind = ? AND status = 'queued' AND expires_at > ?" . ($taskId === null ? '' : ' AND task_id = ?')
                . ' ORDER BY created_at FOR UPDATE',
                array_merge([$agentId, $kind, Time::now()], $taskId === null ? [] : [$taskId])
            );
            if (in_array($kind, self::IDEMPOTENT, true)) {
                foreach ($waiting as $row) {
                    if (json_decode($row['payload'], true) == json_decode(json_encode($payload), true)) {
                        return $row['id'];
                    }
                }
                $waiting = [];
            }
            if ($kind === 'task.upsert') {
                foreach ($waiting as $row) {
                    $old = json_decode($row['payload'], false);
                    if (isset($old->secrets) && is_object($old->secrets)) {
                        $payload->secrets = is_object($payload->secrets ?? null) ? $payload->secrets : new \stdClass();
                        foreach (get_object_vars($old->secrets) as $credentialId => $secret) {
                            if (!property_exists($payload->secrets, $credentialId)) {
                                $payload->secrets->$credentialId = $secret;
                            }
                        }
                    }
                }
            }
            $id = $this->create($agentId, $kind, $payload, self::TTL[$kind], $user['id'] ?? null, $user['login'] ?? null);
            if (in_array($kind, ['task.upsert', 'update.install', 'sevenzip.update'], true)) {
                foreach ($waiting as $row) {
                    $this->db->exec(
                        "UPDATE commands SET status = 'cancelled', superseded_by = ?, result_at = ?, payload = ? WHERE id = ? AND status = 'queued'",
                        [$id, Time::now(), json_encode(self::withoutSecrets(json_decode($row['payload'], false)), self::JSON_FLAGS), $row['id']]
                    );
                }
            }
            return $id;
        });
    }

    /** Withdraws a command that has not been delivered yet. Returns false when it is too late. */
    public function cancel(string $agentId, string $commandId): bool
    {
        return $this->db->transaction(function () use ($agentId, $commandId) {
            $row = $this->db->one(
                "SELECT payload FROM commands WHERE id = ? AND agent_id = ? AND status = 'queued' FOR UPDATE",
                [strtolower($commandId), $agentId]
            );
            if ($row === null) {
                return false;
            }
            $this->db->exec(
                "UPDATE commands SET status = 'cancelled', result_at = ?, payload = ? WHERE id = ?",
                [Time::now(), json_encode(self::withoutSecrets(json_decode($row['payload'], false)), self::JSON_FLAGS), strtolower($commandId)]
            );
            return true;
        });
    }

    public function find(string $id): ?array
    {
        return $this->db->one('SELECT * FROM commands WHERE id = ?', [strtolower($id)]);
    }

    /** Latest commands of a machine, newest first, with the task names. */
    public function forAgent(string $agentId, int $limit = 20): array
    {
        return $this->db->all(
            'SELECT c.*, t.name AS task_name FROM commands c
             LEFT JOIN tasks t ON t.agent_id = c.agent_id AND t.id = c.task_id
             WHERE c.agent_id = ? ORDER BY c.created_at DESC, c.id LIMIT ?',
            [$agentId, $limit]
        );
    }

    public function forTask(string $agentId, string $taskId, int $limit = 10): array
    {
        return $this->db->all(
            'SELECT * FROM commands WHERE agent_id = ? AND task_id = ? ORDER BY created_at DESC, id LIMIT ?',
            [$agentId, $taskId, $limit]
        );
    }

    /**
     * The latest edit of a task from the site that the agent has not confirmed yet (queued or delivered), or null.
     * Its payload still holds the task as it was saved; secrets are erased once delivered.
     */
    public function pendingUpsert(string $agentId, string $taskId): ?array
    {
        return $this->db->one(
            "SELECT * FROM commands WHERE agent_id = ? AND task_id = ? AND kind = 'task.upsert' AND status IN ('queued', 'delivered')
             AND expires_at > ? ORDER BY created_at DESC LIMIT 1",
            [$agentId, $taskId, Time::now()]
        );
    }

    /** Commands of one kind that wait for the agents (queued or delivered), by agent id. */
    public function waiting(string $kind): array
    {
        $result = [];
        foreach ($this->db->all(
            "SELECT agent_id, payload, status, created_at FROM commands WHERE kind = ? AND status IN ('queued', 'delivered') AND expires_at > ?
             ORDER BY created_at",
            [$kind, Time::now()]
        ) as $row) {
            $result[$row['agent_id']] = $row + ['data' => json_decode($row['payload'], true) ?: []];
        }
        return $result;
    }

    /**
     * Takes the queued, unexpired commands of an agent for the heartbeat response and marks them delivered.
     * Encrypted secrets are erased from the stored payload once delivered (section 4).
     *
     * @return list<array{id: string, kind: string, payload: object, expires_at: string}>
     */
    public function deliver(string $agentId, bool $remoteRestore = false): array
    {
        return $this->db->transaction(function () use ($agentId, $remoteRestore) {
            $rows = $this->db->all(
                "SELECT id, kind, payload, expires_at FROM commands
                 WHERE agent_id = ? AND status = 'queued' AND expires_at > ? AND restore_obsolete = 0"
                 . ($remoteRestore ? '' : " AND kind NOT LIKE 'restore.%'") . ' ORDER BY created_at, id FOR UPDATE',
                [$agentId, Time::now()]
            );
            $now = Time::now();
            $result = [];
            foreach ($rows as $row) {
                $payload = json_decode($row['payload'], false) ?? new \stdClass();
                $result[] = ['id' => $row['id'], 'kind' => $row['kind'], 'payload' => $payload, 'expires_at' => Time::iso($row['expires_at'])];
                $this->db->exec(
                    "UPDATE commands SET status = 'delivered', delivered_at = ?, payload = ? WHERE id = ?",
                    [$now, json_encode(self::withoutSecrets($payload), self::JSON_FLAGS), $row['id']]
                );
            }
            return $result;
        });
    }

    /** commands/{id}/result. A repeated result of a finished command changes nothing. */
    public function result(string $agentId, string $commandId, Input $in): void
    {
        $status = $in->enum('status', self::RESULT_STATUSES);
        $command = $this->db->one('SELECT agent_id, kind, task_id, payload, status, delivered_at, restore_obsolete FROM commands WHERE id = ?', [strtolower($commandId)]);
        if ($command === null || $command['agent_id'] !== $agentId) {
            throw new InvalidInput("command $commandId is not known for this agent");
        }
        $restore = in_array($command['kind'], RemoteRestore::COMMANDS, true);
        if ($restore && ($command['delivered_at'] === null || !empty($command['restore_obsolete']))) {
            throw new InvalidInput('restore command was not delivered to this registration');
        }
        if (in_array($command['status'], ['done', 'failed', 'rejected'], true)) {
            return;
        }
        $payload = json_decode($command['payload'], true);
        $data = $restore ? RemoteRestore::result($command['kind'], $command['kind'] === RemoteRestore::START
            ? strtolower($commandId) : ($payload['job_id'] ?? null), $status, $in) : null;
        $message = $restore ? null : $in->string('message', 65535, true);
        $changed = $this->db->exec(
            "UPDATE commands SET status = ?, result_message = ?, result_data = ?, result_at = ?
             WHERE id = ? AND restore_obsolete = 0 AND status NOT IN ('done', 'failed', 'rejected')",
            [$status, $message, $data === null ? null : json_encode($data, self::JSON_FLAGS), Time::now(), strtolower($commandId)]
        );
        if ($changed !== 0 && $status !== 'done') {
            $taskId = $command['task_id'] ?? ($payload['task_id'] ?? null);
            $this->notices->add('command.failed', $agentId, is_string($taskId) ? $taskId : null,
                ['kind' => $command['kind'], 'status' => $status, 'message' => (string) $message]);
        }
    }

    /**
     * Commands whose time is up become expired, and the secrets still in them are erased (section 11).
     * A delivered command that never reported back expires too; a late result still overrides it.
     */
    public function expire(int $now): int
    {
        $rows = $this->db->all(
            "SELECT id, payload FROM commands WHERE status IN ('queued', 'delivered') AND expires_at <= ?",
            [Time::db($now)]
        );
        foreach ($rows as $row) {
            $payload = json_decode($row['payload'], false) ?? new \stdClass();
            $this->db->exec(
                "UPDATE commands SET status = 'expired', payload = ? WHERE id = ? AND status IN ('queued', 'delivered')",
                [json_encode(self::withoutSecrets($payload), self::JSON_FLAGS), $row['id']]
            );
        }
        return count($rows);
    }

    /** The task a command is about: payload.task_id, or the id of the task in task.upsert. */
    private static function taskIdOf(string $kind, object $payload): ?string
    {
        $id = $kind === 'task.upsert' ? ($payload->task->id ?? null) : ($payload->task_id ?? null);
        return is_string($id) && preg_match('/^[0-9a-f-]{36}$/i', $id) ? strtolower($id) : null;
    }

    private static function withoutSecrets(object $payload): object
    {
        $copy = json_decode(json_encode($payload), false);
        if (isset($copy->secrets) && is_object($copy->secrets)) {
            foreach (get_object_vars($copy->secrets) as $secret) {
                if (is_object($secret) && property_exists($secret, 'secret_enc')) {
                    $secret->secret_enc = null;
                }
            }
        }
        return $copy;
    }
}
