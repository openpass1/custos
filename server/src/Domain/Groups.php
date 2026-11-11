<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Machine groups: servers, workstations, departments (section 5, 7.2).
 */
final class Groups
{
    public const MAX_NAME = 100;

    public function __construct(private readonly Db $db)
    {
    }

    /** @return list<array{id: int, name: string, agent_count: int}> */
    public function all(): array
    {
        return $this->db->all(
            'SELECT g.id, g.name, (SELECT COUNT(*) FROM agents a WHERE a.group_id = g.id) AS agent_count
             FROM agent_groups g ORDER BY g.name'
        );
    }

    public function find(int $id): ?array
    {
        return $this->db->one('SELECT * FROM agent_groups WHERE id = ?', [$id]);
    }

    /** @throws \InvalidArgumentException with a language key */
    public function create(string $name): int
    {
        $name = $this->checkName($name, null);
        $this->db->exec('INSERT INTO agent_groups (name, created_at) VALUES (?, ?)', [$name, Time::now()]);
        return $this->db->lastInsertId();
    }

    /** @throws \InvalidArgumentException with a language key */
    public function rename(int $id, string $name): void
    {
        $name = $this->checkName($name, $id);
        $this->db->exec('UPDATE agent_groups SET name = ? WHERE id = ?', [$name, $id]);
    }

    /** Machines of a deleted group stay, without a group. */
    public function delete(int $id): void
    {
        $this->db->exec('DELETE FROM agent_groups WHERE id = ?', [$id]);
    }

    private function checkName(string $name, ?int $id): string
    {
        $name = trim($name);
        if ($name === '' || mb_strlen($name) > self::MAX_NAME) {
            throw new \InvalidArgumentException('groups.error.name');
        }
        $existing = $this->db->value('SELECT id FROM agent_groups WHERE name = ?', [$name]);
        if ($existing !== null && (int) $existing !== $id) {
            throw new \InvalidArgumentException('groups.error.exists');
        }
        return $name;
    }
}
