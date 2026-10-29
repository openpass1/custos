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

    public function __construct(private readonly Db $db)
    {
    }

    /** Queues a command and returns its id. */
    public function create(string $agentId, string $kind, object|array $payload, int $ttlSeconds, ?int $userId = null): string
    {
        $id = Crypto::uuid4();
        $this->db->exec(
            'INSERT INTO commands (id, agent_id, kind, payload, created_by, created_at, expires_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$id, $agentId, $kind, json_encode((object) $payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES),
             $userId, Time::now(), Time::db(time() + $ttlSeconds)]
        );
        return $id;
    }

    /**
     * Takes the queued, unexpired commands of an agent for the heartbeat response and marks them delivered.
     * Encrypted secrets are erased from the stored payload once delivered (section 4).
     *
     * @return list<array{id: string, kind: string, payload: object, expires_at: string}>
     */
    public function deliver(string $agentId): array
    {
        return $this->db->transaction(function () use ($agentId) {
            $rows = $this->db->all(
                "SELECT id, kind, payload, expires_at FROM commands
                 WHERE agent_id = ? AND status = 'queued' AND expires_at > ?
                 ORDER BY created_at, id FOR UPDATE",
                [$agentId, Time::now()]
            );
            $now = Time::now();
            $result = [];
            foreach ($rows as $row) {
                $payload = json_decode($row['payload'], false) ?? new \stdClass();
                $result[] = ['id' => $row['id'], 'kind' => $row['kind'], 'payload' => $payload, 'expires_at' => Time::iso($row['expires_at'])];
                $this->db->exec(
                    "UPDATE commands SET status = 'delivered', delivered_at = ?, payload = ? WHERE id = ?",
                    [$now, json_encode(self::withoutSecrets($payload), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES), $row['id']]
                );
            }
            return $result;
        });
    }

    /** commands/{id}/result. A repeated result of a finished command changes nothing. */
    public function result(string $agentId, string $commandId, Input $in): void
    {
        $status = $in->enum('status', self::RESULT_STATUSES);
        $message = $in->string('message', 65535, true);
        $command = $this->db->one('SELECT agent_id, status FROM commands WHERE id = ?', [strtolower($commandId)]);
        if ($command === null || $command['agent_id'] !== $agentId) {
            throw new InvalidInput("command $commandId is not known for this agent");
        }
        if (in_array($command['status'], ['done', 'failed', 'rejected'], true)) {
            return;
        }
        $this->db->exec(
            'UPDATE commands SET status = ?, result_message = ?, result_at = ? WHERE id = ?',
            [$status, $message, Time::now(), strtolower($commandId)]
        );
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
