<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Quiet notices on the site, the bell (7.7). They are marked read by any user (all users are administrators) and are
 * never sent to Telegram or by e-mail.
 *
 * Kinds and their params:
 *   agent.pending      {name}               a new machine waits for approval
 *   agent.reregistered {name, old_name}     a known machine registered again with a new key
 *   task.created       {name, by}           a task was created on the machine
 *   task.changed       {name, by, revision} a task was changed on the machine (updated_by = local:…)
 *   task.deleted       {name}               a task was deleted on the machine
 *   task.conflict      {name, revision, by} an edit from the site overwrote this version on the machine (A.4)
 *   command.failed     {kind, status, message} a command was rejected or failed
 */
final class Notices
{
    public function __construct(private readonly Db $db)
    {
    }

    public function add(string $kind, ?string $agentId, ?string $taskId, array $params = []): void
    {
        $this->db->exec(
            'INSERT INTO notices (created_at, kind, agent_id, task_id, params) VALUES (?, ?, ?, ?, ?)',
            [Time::now(), $kind, $agentId, $taskId, json_encode($params, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)]
        );
    }

    public function unreadCount(): int
    {
        return (int) $this->db->value('SELECT COUNT(*) FROM notices WHERE read_at IS NULL');
    }

    /**
     * @return list<array> newest first, with the agent name (hostname, display_name) when the agent still exists
     */
    public function latest(int $limit, int $offset = 0, bool $unreadOnly = false): array
    {
        return $this->db->all(
            'SELECT n.*, a.hostname, a.display_name FROM notices n LEFT JOIN agents a ON a.id = n.agent_id'
            . ($unreadOnly ? ' WHERE n.read_at IS NULL' : '')
            . ' ORDER BY n.created_at DESC, n.id DESC LIMIT ? OFFSET ?',
            [$limit, $offset]
        );
    }

    public function count(): int
    {
        return (int) $this->db->value('SELECT COUNT(*) FROM notices');
    }

    /** Marks the unread notices of these kinds about a machine as read: what they asked for is done (an approval). */
    public function settle(string $agentId, array $kinds): void
    {
        $marks = implode(', ', array_fill(0, count($kinds), '?'));
        $this->db->exec(
            "UPDATE notices SET read_at = ? WHERE agent_id = ? AND kind IN ($marks) AND read_at IS NULL",
            [Time::now(), $agentId, ...$kinds]
        );
    }

    public function markRead(?int $id, int $userId): void
    {
        if ($id === null) {
            $this->db->exec('UPDATE notices SET read_at = ?, read_by = ? WHERE read_at IS NULL', [Time::now(), $userId]);
        } else {
            $this->db->exec('UPDATE notices SET read_at = ?, read_by = ? WHERE id = ? AND read_at IS NULL', [Time::now(), $userId, $id]);
        }
    }
}
