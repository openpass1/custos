<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Db;
use Custos\Infra\Time;

/**
 * Snapshots of the volumes of a Linux machine (inventory field "snapshots", A.8): whether each volume that holds
 * sources of tasks with snapshots on can have one, and why not. CUSTOS for Linux makes LVM, btrfs and ZFS snapshots;
 * an LVM snapshot takes its room from the free space of the volume group, which the usual Ubuntu install gives
 * entirely to the volume.
 *
 * The site keeps what the agent last said (agents.snapshots, JSON) and shows it on the machine page with the fix in
 * the site's language. A volume without room for a snapshot, or whose last snapshot ran full, opens a "snapshot"
 * problem for each task on it (one per task, like "space"): it is on the dashboard and goes out by Telegram or e-mail
 * once. It closes when the agent says the volume is fine again or no longer lists the task there (snapshots turned
 * off, the task disabled or deleted). A volume that cannot have snapshots at all (a plain partition) or has little
 * room is only shown on the machine page: the first is fixed by turning snapshots off in the task, and the task warns
 * at each run anyway.
 */
final class Snapshots
{
    /** The states that open a problem, the worse first. */
    public const PROBLEM_STATES = ['overflowed', 'no_space'];
    /** The states the site knows (A.8); another state of a newer agent is shown with the agent's own words. */
    public const STATES = ['ok', 'low_space', 'no_space', 'overflowed', 'unsupported'];

    public function __construct(private readonly Db $db)
    {
    }

    /**
     * Reads the field "snapshots" of an inventory, or null when the agent did not send it (Windows, macOS).
     *
     * @return ?list<array<string, mixed>>
     */
    public static function read(Input $in): ?array
    {
        if (!$in->has('snapshots') || $in->raw('snapshots') === null) {
            return null;
        }
        $volumes = [];
        foreach ($in->objects('snapshots') as $s) {
            $volumes[] = [
                'mount' => $s->string('mount', 1024),
                'device' => $s->string('device', 1024, true),
                'file_system' => $s->string('file_system', 32, true),
                'method' => $s->code('method'),
                'state' => $s->code('state'),
                'volume_group' => $s->string('volume_group', 255, true),
                'logical_volume' => $s->string('logical_volume', 255, true),
                'free_bytes' => $s->int('free_bytes', true),
                'volume_bytes' => $s->int('volume_bytes', true),
                'last_size_bytes' => $s->int('last_size_bytes', true),
                'last_used_bytes' => $s->int('last_used_bytes', true),
                'last_run_at' => $s->time('last_run_at', true),
                'detail' => $s->string('detail', 4000, true),
                'task_ids' => $s->uuids('task_ids'),
            ];
        }
        return $volumes;
    }

    /**
     * Keeps what the agent said and opens, updates or closes its "snapshot" problems.
     *
     * @param list<array<string, mixed>> $volumes from read()
     * @return array{opened: int, closed: int}
     */
    public function store(string $agentId, array $volumes, int $now): array
    {
        $nowDb = Time::db($now);
        // The worst problem of each task: the volumes it reads from that cannot have a snapshot.
        $worst = [];
        foreach ($volumes as $v) {
            $rank = array_search($v['state'], self::PROBLEM_STATES, true);
            if ($rank === false) {
                continue;
            }
            foreach ($v['task_ids'] as $taskId) {
                if (!isset($worst[$taskId]) || $rank < $worst[$taskId]['rank']) {
                    $worst[$taskId] = ['rank' => $rank, 'message' => self::message($v)];
                }
            }
        }
        $stats = ['opened' => 0, 'closed' => 0];
        $this->db->transaction(function () use ($agentId, $volumes, $worst, $nowDb, &$stats) {
            $this->db->exec('UPDATE agents SET snapshots = ?, snapshots_at = ? WHERE id = ?',
                [json_encode($volumes, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE), $nowDb, $agentId]);
            foreach ($this->db->all("SELECT id, task_id FROM problems WHERE agent_id = ? AND kind = 'snapshot' AND closed_at IS NULL", [$agentId]) as $open) {
                if (isset($worst[$open['task_id']])) {
                    $this->db->exec('UPDATE problems SET message = ?, last_at = ? WHERE id = ?',
                        [$worst[$open['task_id']]['message'], $nowDb, $open['id']]);
                    unset($worst[$open['task_id']]);
                    continue;
                }
                $stats['closed'] += $this->db->exec(
                    "UPDATE problems SET closed_at = ?, closed_by = 'snapshot_ok' WHERE id = ? AND closed_at IS NULL",
                    [$nowDb, $open['id']]
                );
            }
            foreach ($worst as $taskId => $problem) {
                // Only for tasks the site knows: an agent may list one the site has not heard of yet.
                if ($this->db->one('SELECT 1 AS x FROM tasks WHERE agent_id = ? AND id = ? AND deleted = 0', [$agentId, $taskId]) === null) {
                    continue;
                }
                // The unique key on open problems keeps this to one per task.
                $stats['opened'] += $this->db->exec(
                    "INSERT IGNORE INTO problems (agent_id, task_id, kind, opened_at, last_at, message)
                     VALUES (?, ?, 'snapshot', ?, ?, ?)",
                    [$agentId, $taskId, $nowDb, $nowDb, $problem['message']]
                );
            }
        });
        return $stats;
    }

    /**
     * The volumes of a machine as the agent last said, or null when it never sent them.
     *
     * @return ?list<array<string, mixed>>
     */
    public static function of(array $agent): ?array
    {
        if (($agent['snapshots'] ?? null) === null) {
            return null;
        }
        $volumes = json_decode((string) $agent['snapshots'], true);
        return is_array($volumes) ? array_values(array_filter($volumes, 'is_array')) : null;
    }

    /** What a "snapshot" problem keeps in its message: the state, the mount point and the volume group, as JSON. */
    public static function message(array $volume): string
    {
        return json_encode([
            'state' => (string) $volume['state'],
            'mount' => (string) $volume['mount'],
            'group' => $volume['volume_group'] ?? null,
        ], JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    }

    /**
     * The message of a "snapshot" problem read back: state, mount, group.
     *
     * @return array{state: string, mount: string, group: ?string}
     */
    public static function problem(?string $message): array
    {
        $data = json_decode((string) $message, true);
        return [
            'state' => is_array($data) ? (string) ($data['state'] ?? '') : '',
            'mount' => is_array($data) ? (string) ($data['mount'] ?? '') : (string) $message,
            'group' => is_array($data) && is_string($data['group'] ?? null) ? $data['group'] : null,
        ];
    }

    /** The command that adds a new disk to a volume group: the fix for no room and for a snapshot that ran full. */
    public static function fixCommand(?string $group): string
    {
        $group = $group === null || $group === '' ? '<vg>' : $group;
        return "pvcreate /dev/sdX && vgextend $group /dev/sdX";
    }

    /** The colour of a state: status_class() names. */
    public static function status(string $state): string
    {
        return match ($state) {
            'ok' => 'ok',
            'low_space', 'unsupported' => 'warning',
            'no_space', 'overflowed' => 'error',
            default => 'muted',
        };
    }
}
