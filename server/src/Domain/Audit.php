<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Audit log of user actions (section 4).
 */
final class Audit
{
    public function __construct(private readonly Db $db)
    {
    }

    /**
     * @param array{id: int, login: string}|null $user null for actions without a signed-in user (failed logins)
     */
    public function log(
        ?array $user,
        string $ip,
        string $action,
        ?string $objectType = null,
        ?string $objectId = null,
        array $details = [],
        ?string $login = null
    ): void {
        $this->db->exec(
            'INSERT INTO audit_log (created_at, user_id, login, ip, action, object_type, object_id, details)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
            [
                Time::now(),
                $user['id'] ?? null,
                mb_substr($login ?? $user['login'] ?? '', 0, 64),
                $ip,
                $action,
                $objectType,
                $objectId,
                $details === [] ? null : json_encode($details, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES),
            ]
        );
    }

    /** Failed logins for this IP and login during the last $minutes minutes. */
    public function recentFailures(string $ip, string $login, int $minutes): int
    {
        return (int) $this->db->value(
            "SELECT COUNT(*) FROM audit_log WHERE action = 'login.failed' AND ip = ? AND login = ? AND created_at > ?",
            [$ip, mb_substr($login, 0, 64), Time::db(time() - $minutes * 60)]
        );
    }

    /**
     * @param array{from?: ?string, to?: ?string, user_id?: ?int} $filter from/to are UTC "Y-m-d H:i:s"
     * @return array{rows: list<array>, total: int}
     */
    public function search(array $filter, int $limit, int $offset): array
    {
        $where = [];
        $params = [];
        if (!empty($filter['from'])) {
            $where[] = 'created_at >= ?';
            $params[] = $filter['from'];
        }
        if (!empty($filter['to'])) {
            $where[] = 'created_at < ?';
            $params[] = $filter['to'];
        }
        if (!empty($filter['user_id'])) {
            $where[] = 'user_id = ?';
            $params[] = (int) $filter['user_id'];
        }
        $sqlWhere = $where === [] ? '' : 'WHERE ' . implode(' AND ', $where);
        $total = (int) $this->db->value("SELECT COUNT(*) FROM audit_log $sqlWhere", $params);
        $rows = $this->db->all(
            "SELECT * FROM audit_log $sqlWhere ORDER BY created_at DESC, id DESC LIMIT ? OFFSET ?",
            [...$params, $limit, $offset]
        );
        return ['rows' => $rows, 'total' => $total];
    }
}
