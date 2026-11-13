<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Crypto;
use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Task templates (section 8). A template is a task object with its own ids plus the accounts it needs, without
 * passwords. Applied to a machine it becomes a task of that machine through task.upsert. Every id is turned into a
 * machine id the same way each time (Crypto::uuidFrom), so applying the template again updates the same task, and the
 * accounts it created are reused.
 */
final class Templates
{
    private const JSON_FLAGS = JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES;

    public function __construct(private readonly Db $db, private readonly TaskEdits $edits)
    {
    }

    /** All templates with the number of machines they were applied to and how many of those have an older revision. */
    public function all(): array
    {
        return $this->db->all(
            'SELECT t.*, COUNT(l.agent_id) AS machines, COALESCE(SUM(l.template_revision < t.revision), 0) AS outdated
             FROM templates t LEFT JOIN template_links l ON l.template_id = t.id
             GROUP BY t.id ORDER BY t.name'
        );
    }

    public function find(int $id): ?array
    {
        $row = $this->db->one('SELECT * FROM templates WHERE id = ?', [$id]);
        if ($row === null) {
            return null;
        }
        $row['task'] = TaskEdits::decode($row['data']);
        $row['credential_list'] = self::credentialMap((string) $row['credentials']);
        return $row;
    }

    /** @return array<string, array{id: string, kind: string, user: string, domain: ?string}> */
    public static function credentialMap(string $json): array
    {
        $result = [];
        foreach ((array) json_decode($json, true) as $c) {
            if (is_array($c) && is_string($c['id'] ?? null)) {
                $result[$c['id']] = ['id' => $c['id'], 'kind' => (string) ($c['kind'] ?? ''), 'user' => (string) ($c['user'] ?? ''),
                                     'domain' => $c['domain'] ?? null, 'has_secret' => false];
            }
        }
        return $result;
    }

    /**
     * Creates ($id = null) or updates a template. Only the accounts the task refers to are kept.
     *
     * @param array<string, array> $credentials template accounts by id
     * @return int the template id
     */
    public function save(?int $id, array $task, array $credentials, string $login): int
    {
        $refs = TaskForm::credentialRefs($task);
        $kept = [];
        foreach ($refs as $ref) {
            if (isset($credentials[$ref])) {
                $c = $credentials[$ref];
                $kept[] = ['id' => $ref, 'kind' => (string) $c['kind'], 'user' => (string) ($c['user'] ?? ''), 'domain' => $c['domain'] ?? null];
            }
        }
        $name = (string) $task['name'];
        unset($task['revision'], $task['updated_at'], $task['updated_by']);
        $data = json_encode(TaskEdits::object($task), self::JSON_FLAGS);
        $now = Time::now();
        if ($this->db->value('SELECT id FROM templates WHERE name = ?' . ($id === null ? '' : ' AND id <> ?'),
                array_merge([$name], $id === null ? [] : [$id])) !== null) {
            throw new \InvalidArgumentException('templates.error.exists');
        }
        if ($id === null) {
            $this->db->exec(
                'INSERT INTO templates (uuid, name, data, credentials, created_at, created_by, updated_at, updated_by)
                 VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
                [Crypto::uuid4(), $name, $data, json_encode($kept, self::JSON_FLAGS), $now, $login, $now, $login]
            );
            return $this->db->lastInsertId();
        }
        $this->db->exec(
            'UPDATE templates SET name = ?, data = ?, credentials = ?, revision = revision + 1, updated_at = ?, updated_by = ? WHERE id = ?',
            [$name, $data, json_encode($kept, self::JSON_FLAGS), $now, $login, $id]
        );
        return $id;
    }

    public function delete(int $id): void
    {
        $this->db->exec('DELETE FROM templates WHERE id = ?', [$id]);
    }

    /**
     * A template made from a task of a machine: the task under new ids, and its accounts copied without passwords.
     *
     * @return array{task: array, credentials: array<string, array>}
     */
    public function fromTask(array $agent, array $task): array
    {
        $copy = TaskForm::copy($task);
        $machine = TaskEdits::credentials($agent);
        $map = [];
        $credentials = [];
        foreach (TaskForm::credentialRefs($copy) as $ref) {
            if (!isset($machine[$ref]) || in_array($machine[$ref]['kind'] ?? '', TaskForm::CLOUD_KINDS, true)) {
                // A cloud drive account stays on its machine: the template takes the one connected on each machine.
                continue;
            }
            $id = Crypto::uuid4();
            $map[$ref] = $id;
            $credentials[$id] = ['id' => $id, 'kind' => (string) $machine[$ref]['kind'], 'user' => (string) ($machine[$ref]['user'] ?? ''),
                                 'domain' => $machine[$ref]['domain'] ?? null, 'has_secret' => false];
        }
        return ['task' => self::mapCredentials($copy, fn($id) => $map[$id] ?? null), 'credentials' => $credentials];
    }

    /** Machines the template was applied to, with the state of their task. */
    public function links(int $templateId): array
    {
        return $this->db->all(
            'SELECT l.*, a.hostname, a.display_name, a.status AS agent_status, t.name AS task_name, t.deleted AS task_deleted,
                    c.status AS command_status, c.result_message
             FROM template_links l
             JOIN agents a ON a.id = l.agent_id
             LEFT JOIN tasks t ON t.agent_id = l.agent_id AND t.id = l.task_id
             LEFT JOIN commands c ON c.id = l.command_id
             WHERE l.template_id = ? ORDER BY COALESCE(a.display_name, a.hostname)',
            [$templateId]
        );
    }

    /** The template whose task this is, if any: [id, name, revision, template_revision]. */
    public function linkOf(string $agentId, string $taskId): ?array
    {
        return $this->db->one(
            'SELECT t.id, t.name, t.revision, l.template_revision FROM template_links l JOIN templates t ON t.id = l.template_id
             WHERE l.agent_id = ? AND l.task_id = ?',
            [$agentId, $taskId]
        );
    }

    /** The id an item of the template gets on a machine. */
    public static function machineId(array $template, string $agentId, string $id): string
    {
        return Crypto::uuidFrom("custos-template\n{$template['uuid']}\n$agentId\n" . strtolower($id));
    }

    /** The template's task as it is sent to one machine. */
    public static function machineTask(array $template, string $agentId): array
    {
        $map = fn(string $id) => self::machineId($template, $agentId, $id);
        $task = $template['task'];
        $task['id'] = $map((string) $template['uuid']);
        $task['name'] = $template['name'];
        foreach (TaskForm::ITEM_LISTS as $list) {
            foreach ((array) ($task[$list] ?? []) as $i => $item) {
                if (is_array($item) && is_string($item['id'] ?? null)) {
                    $task[$list][$i]['id'] = $map($item['id']);
                }
            }
        }
        return self::mapCredentials($task, $map);
    }

    /**
     * Applies the template to machines. Passwords are needed for the template's accounts, except on machines that
     * already have that account from an earlier application.
     *
     * @param list<array> $agents agent rows
     * @param array<string, string> $passwords template account id => password (may be empty)
     * @param array{id: int, login: string} $user
     * @return list<array{agent: array, command: ?string, error: ?array{key: string, params: array}}>
     */
    public function apply(array $template, array $agents, array $passwords, array $user): array
    {
        $results = [];
        foreach ($agents as $agent) {
            $error = null;
            $command = null;
            if ($agent['status'] !== 'approved') {
                $error = ['key' => 'templates.apply.not_approved', 'params' => []];
            } elseif (TaskEdits::remoteEditOff($agent)) {
                $error = ['key' => 'templates.apply.remote_edit_off', 'params' => []];
            } else {
                $task = self::machineTask($template, $agent['id']);
                $existing = TaskEdits::credentials($agent);
                $error = $this->cloudAccounts($agent, $task, $existing);
                $secrets = [];
                foreach ($error === null ? TaskForm::credentialRefs($template['task']) : [] as $ref) {
                    $c = $template['credential_list'][$ref] ?? null;
                    if ($c === null) {
                        continue;
                    }
                    $machineId = self::machineId($template, $agent['id'], $ref);
                    $password = (string) ($passwords[$ref] ?? '');
                    if ($password !== '' || ($c['kind'] === 'db' && !isset($existing[$machineId]))) {
                        // A database account may have no password: it is created with an empty one.
                        $secrets[$machineId] = ['kind' => $c['kind'], 'user' => $c['user'], 'domain' => $c['domain'], 'password' => $password];
                    } elseif (empty($existing[$machineId]['has_secret']) && $c['kind'] !== 'db') {
                        $error = ['key' => 'templates.apply.no_password', 'params' => ['account' => self::accountName($c)]];
                        break;
                    }
                }
                if ($error === null) {
                    $command = $this->edits->upsert($agent, $task, $this->edits->baseRevision($agent, $task['id']), $secrets, $user);
                    $values = [$task['id'], $template['revision'], $command, Time::now(), $user['login']];
                    $this->db->exec(
                        'INSERT INTO template_links (template_id, agent_id, task_id, template_revision, command_id, applied_at, applied_by)
                         VALUES (?, ?, ?, ?, ?, ?, ?)
                         ON DUPLICATE KEY UPDATE task_id = ?, template_revision = ?, command_id = ?, applied_at = ?, applied_by = ?',
                        [$template['id'], $agent['id'], ...$values, ...$values]
                    );
                }
            }
            $results[] = ['agent' => $agent, 'command' => $command, 'error' => $error];
        }
        return $results;
    }

    /**
     * A template names no cloud drive account (7.5): each machine uses the account of that service connected on it.
     * One the machine's task already uses is kept (the administrator may have chosen among several).
     *
     * @return array{key: string, params: array}|null what is missing, or null
     */
    private function cloudAccounts(array $agent, array &$task, array $existing): ?array
    {
        $before = [];
        foreach ((array) ($this->edits->current($agent, (string) $task['id'])['task']['destinations'] ?? []) as $d) {
            if (is_array($d) && is_string($d['id'] ?? null)) {
                $before[$d['id']] = $d['credential_id'] ?? null;
            }
        }
        foreach ((array) ($task['destinations'] ?? []) as $i => $d) {
            $kind = is_array($d) ? (string) ($d['kind'] ?? '') : '';
            if (!in_array($kind, TaskForm::CLOUD_KINDS, true)) {
                continue;
            }
            $kept = $before[(string) ($d['id'] ?? '')] ?? null;
            if (is_string($kept) && ($existing[$kept]['kind'] ?? null) === $kind) {
                $task['destinations'][$i]['credential_id'] = $kept;
                continue;
            }
            $accounts = array_values(array_filter($existing, fn($c) => ($c['kind'] ?? null) === $kind));
            if (count($accounts) !== 1) {
                return ['key' => $accounts === [] ? 'templates.apply.cloud_account' : 'templates.apply.cloud_accounts',
                        'params' => ['service' => ['key' => 'credential.kind.' . $kind, 'params' => []]]];
            }
            $task['destinations'][$i]['credential_id'] = (string) $accounts[0]['id'];
        }
        return null;
    }

    public static function accountName(array $c): string
    {
        if (($c['kind'] ?? '') === 'archive_password') {
            return 'archive_password';
        }
        $domain = (string) ($c['domain'] ?? '');
        return ($domain !== '' && $domain !== '.' ? $domain . '\\' : '') . ($c['user'] ?? '');
    }

    /** Replaces the account ids a task refers to; $map returns the new id or null to drop the reference. */
    private static function mapCredentials(array $task, callable $map): array
    {
        foreach (TaskForm::ITEM_LISTS as $list) {
            foreach ((array) ($task[$list] ?? []) as $i => $item) {
                if (is_array($item) && is_string($item['credential_id'] ?? null)) {
                    $task[$list][$i]['credential_id'] = $map($item['credential_id']);
                }
            }
        }
        if (is_string($task['archive']['password_credential_id'] ?? null)) {
            $task['archive']['password_credential_id'] = $map($task['archive']['password_credential_id']);
        }
        return $task;
    }
}
