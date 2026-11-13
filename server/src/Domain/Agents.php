<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Crypto;
use Custos\Infra\Db;
use Custos\Infra\Time;
use OpenSSLAsymmetricKey;

/**
 * Agents: registration (A.3), heartbeat state (A.4) and what administrators do with them (section 8).
 */
final class Agents
{
    public const MIN_KEY_BITS = 3072;

    public function __construct(private readonly Db $db, private readonly Monitor $monitor, private readonly Notices $notices)
    {
    }

    public function find(string $id): ?array
    {
        return $this->db->one('SELECT * FROM agents WHERE id = ?', [strtolower($id)]);
    }

    /** All agents with their task counts and group names, pending ones first. */
    public function all(): array
    {
        return $this->db->all(
            "SELECT a.*, g.name AS group_name,
                    (SELECT COUNT(*) FROM tasks t WHERE t.agent_id = a.id AND t.deleted = 0) AS task_count
             FROM agents a LEFT JOIN agent_groups g ON g.id = a.group_id
             ORDER BY a.status = 'pending' DESC, COALESCE(a.display_name, a.hostname), a.first_seen_at"
        );
    }

    /**
     * Registers an agent and returns ['id' => ..., 'status' => ...].
     * The same public key always maps to the same agent, so a repeated registration does not create duplicates.
     * A new key on a machine that is already known becomes a pending re-registration of that machine.
     *
     * @param array{hostname: string, machine_guid: string, os: string, agent_version: string, time_zone: string} $info
     */
    public function register(array $info, OpenSSLAsymmetricKey $publicKey, string $ip): array
    {
        $sha = hash('sha256', Crypto::publicDer($publicKey));
        return $this->db->transaction(function () use ($info, $publicKey, $sha, $ip) {
            $existing = $this->db->one('SELECT id, status FROM agents WHERE key_sha256 = ? FOR UPDATE', [$sha]);
            if ($existing !== null) {
                $this->db->exec(
                    'UPDATE agents SET hostname = ?, machine_guid = ?, os = ?, agent_version = ?, time_zone = ?, last_ip = ?
                     WHERE id = ?',
                    [$info['hostname'], $info['machine_guid'], $info['os'], $info['agent_version'], $info['time_zone'], $ip, $existing['id']]
                );
                return $existing;
            }
            $previous = $this->db->one(
                "SELECT id, hostname, display_name FROM agents WHERE machine_guid = ? AND status <> 'pending' ORDER BY first_seen_at DESC LIMIT 1",
                [$info['machine_guid']]
            );
            $id = Crypto::uuid4();
            $this->db->exec(
                'INSERT INTO agents (id, status, hostname, machine_guid, public_key, key_sha256, key_fingerprint, reregistration_of,
                                     os, agent_version, time_zone, last_ip, first_seen_at)
                 VALUES (?, \'pending\', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
                [
                    $id, $info['hostname'], $info['machine_guid'], Crypto::publicPem($publicKey), $sha,
                    Crypto::fingerprint($publicKey), $previous['id'] ?? null, $info['os'], $info['agent_version'], $info['time_zone'],
                    $ip, Time::now(),
                ]
            );
            if ($previous === null) {
                $this->notices->add('agent.pending', $id, null, ['name' => $info['hostname']]);
            } else {
                $this->notices->add('agent.reregistered', $id, null, ['name' => $info['hostname'], 'old_name' => self::name($previous)]);
            }
            return ['id' => $id, 'status' => 'pending'];
        });
    }

    /**
     * Stores what the heartbeat says (A.4).
     *
     * @param array{agent_version: string, time_zone: string, service_started_at: ?string, state: string, running: ?array, queue: list<string>, sevenzip_version?: ?string} $hb
     */
    public function heartbeat(string $id, array $hb, string $ip, string $status): void
    {
        $this->monitor->presence($id, $status, time());
        $this->db->exec(
            'UPDATE agents SET last_heartbeat_at = ?, last_ip = ?, agent_version = ?, sevenzip_version = ?, time_zone = ?,
                               service_started_at = ?, state = ?, running = ?, queue = ?
             WHERE id = ?',
            [
                Time::now(), $ip, $hb['agent_version'], $hb['sevenzip_version'] ?? null, $hb['time_zone'], $hb['service_started_at'], $hb['state'],
                $hb['running'] === null ? null : json_encode($hb['running'], JSON_UNESCAPED_SLASHES),
                json_encode($hb['queue'], JSON_UNESCAPED_SLASHES),
                $id,
            ]
        );
        if ($hb['state'] === 'idle') {
            $this->db->exec('DELETE FROM run_progress WHERE agent_id = ?', [$id]);
        }
    }

    public function approve(string $id): void
    {
        $this->db->exec(
            "UPDATE agents SET status = 'approved', approved_at = ?, reregistration_of = NULL WHERE id = ? AND status = 'pending'",
            [Time::now(), $id]
        );
        $this->monitor->watchFrom($id, null, time());
        // "A new machine waits for approval" has done its job: the bell and the dashboard stop repeating it.
        $this->notices->settle($id, ['agent.pending', 'agent.reregistered']);
    }

    /**
     * Approves a pending re-registration by replacing the machine it re-registers (A.3 p.5).
     * The old agent keeps its name, tasks and history but takes the new agent_id and key: the agent already
     * stores the new id, and every table cascades on agents.id. Returns the id of the replaced agent.
     */
    public function replace(string $newId): string
    {
        return $this->db->transaction(function () use ($newId) {
            $new = $this->db->one("SELECT * FROM agents WHERE id = ? AND status = 'pending' FOR UPDATE", [$newId]);
            $oldId = $new['reregistration_of'] ?? null;
            if ($oldId === null || $this->db->one('SELECT id FROM agents WHERE id = ? FOR UPDATE', [$oldId]) === null) {
                throw new \InvalidArgumentException('agents.error.nothing_to_replace');
            }
            $this->db->exec('DELETE FROM agents WHERE id = ?', [$newId]);
            $this->db->exec(
                "UPDATE agents SET id = ?, status = 'approved', approved_at = ?, reregistration_of = NULL,
                        hostname = ?, machine_guid = ?, public_key = ?, key_sha256 = ?, key_fingerprint = ?, os = ?,
                        agent_version = ?, time_zone = ?, last_ip = ?, last_heartbeat_at = ?, service_started_at = ?,
                        state = ?, running = ?, queue = ?, config_revision = NULL
                 WHERE id = ?",
                [
                    $newId, Time::now(), $new['hostname'], $new['machine_guid'], $new['public_key'], $new['key_sha256'],
                    $new['key_fingerprint'], $new['os'], $new['agent_version'], $new['time_zone'], $new['last_ip'],
                    $new['last_heartbeat_at'], $new['service_started_at'], $new['state'], $new['running'], $new['queue'],
                    $oldId,
                ]
            );
            $this->db->exec('UPDATE agents SET reregistration_of = ? WHERE reregistration_of = ?', [$newId, $oldId]);
            $this->monitor->watchFrom($newId, null, time());
            return $oldId;
        });
    }

    public function setDisabled(string $id, bool $disabled): void
    {
        if ($disabled) {
            // Problems of a disabled machine close (6.7), and nothing is expected from it any more.
            $this->db->exec("UPDATE agents SET status = 'disabled' WHERE id = ? AND status = 'approved'", [$id]);
            $this->monitor->sweep($id, time());
        } elseif ($this->db->exec("UPDATE agents SET status = 'approved' WHERE id = ? AND status = 'disabled'", [$id]) > 0) {
            $this->monitor->watchFrom($id, null, time());
        }
    }

    /** Deletes the agent with all its tasks and history. */
    public function delete(string $id): void
    {
        $this->db->transaction(function () use ($id) {
            $this->db->exec('UPDATE agents SET reregistration_of = NULL WHERE reregistration_of = ?', [$id]);
            $this->db->exec('DELETE FROM agents WHERE id = ?', [$id]);
            $this->db->exec('DELETE FROM nonces WHERE scope = ?', [$id]);
        });
    }

    public function rename(string $id, ?string $displayName): void
    {
        $this->db->exec('UPDATE agents SET display_name = ? WHERE id = ?', [$displayName, $id]);
    }

    public function setGroup(string $id, ?int $groupId): void
    {
        $this->db->exec('UPDATE agents SET group_id = ? WHERE id = ?', [$groupId, $id]);
    }

    public static function name(array $agent): string
    {
        return ($agent['display_name'] ?? '') !== '' ? $agent['display_name'] : $agent['hostname'];
    }

    public static function isOnline(array $agent, int $offlineAfterMin): bool
    {
        return $agent['last_heartbeat_at'] !== null
            && Time::unix($agent['last_heartbeat_at']) >= time() - $offlineAfterMin * 60;
    }
}
