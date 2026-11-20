<?php
/**
 * Snapshots of the volumes of a Linux machine (Snapshots, inventory field "snapshots"): whether each volume with
 * sources of tasks with snapshots on can have one, and what to do when it cannot.
 *
 * @var list<array<string, mixed>> $snapshots the volumes as the agent last said (Snapshots::of)
 * @var ?string $snapshotsAt when it said so
 * @var array<string, string> $taskNames names of the machine's tasks by id
 * @var array $agent
 */
use Custos\Domain\Snapshots;

$bytes = fn($n): string => $n === null ? '?' : fmt_bytes((int) $n);
?>
<section class="panel" id="snapshots">
    <h2><?= e(t('snapshots.title')) ?></h2>
    <p class="hint"><?= e(t('snapshots.hint')) ?></p>
    <?php if ($snapshots === []): ?>
        <p class="note"><?= e(t('snapshots.none')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead><tr>
                    <th><?= e(t('snapshots.col.volume')) ?></th>
                    <th><?= e(t('snapshots.col.method')) ?></th>
                    <th><?= e(t('snapshots.col.state')) ?></th>
                    <th><?= e(t('snapshots.col.fix')) ?></th>
                </tr></thead>
                <tbody>
                <?php foreach ($snapshots as $i => $v): ?>
                    <?php
                    $state = (string) ($v['state'] ?? '');
                    $method = (string) ($v['method'] ?? '');
                    $group = is_string($v['volume_group'] ?? null) ? $v['volume_group'] : null;
                    $params = [
                        'mount' => (string) ($v['mount'] ?? ''), 'group' => $group ?? '?', 'free' => $bytes($v['free_bytes'] ?? null),
                        'size' => $bytes($v['last_size_bytes'] ?? null), 'used' => $bytes($v['last_used_bytes'] ?? null),
                        'time' => fmt_time($v['last_run_at'] ?? null), 'fs' => (string) ($v['file_system'] ?? '?'),
                    ];
                    // The fix of a volume group that is too small: a new disk in the group.
                    $command = in_array($state, ['low_space', 'no_space', 'overflowed'], true) && $method === 'lvm' ? Snapshots::fixCommand($group) : null;
                    $text = match (true) {
                        $state === 'ok' && ($v['last_size_bytes'] ?? null) !== null => t('snapshots.fix.ok_last', $params),
                        $state === 'ok' => t('snapshots.fix.ok'),
                        in_array($state, ['low_space', 'no_space', 'overflowed', 'unsupported'], true) => t('snapshots.fix.' . $state, $params),
                        default => (string) ($v['detail'] ?? ''),
                    };
                    $tasks = array_values(array_filter(array_map(fn($id) => isset($taskNames[$id]) ? [$id, $taskNames[$id]] : null, (array) ($v['task_ids'] ?? []))));
                    ?>
                    <tr>
                        <td><strong class="mono"><?= e((string) ($v['mount'] ?? '')) ?></strong>
                            <div class="note mono"><?= e(implode(' · ', array_filter([(string) ($v['device'] ?? ''), (string) ($v['file_system'] ?? ''), ($v['volume_bytes'] ?? null) === null ? '' : $bytes($v['volume_bytes'])]))) ?></div>
                            <?php if ($tasks !== []): ?>
                                <div class="note"><?= e(t('snapshots.tasks')) ?>
                                    <?php foreach ($tasks as $n => [$taskId, $name]): ?><?= $n > 0 ? ', ' : '' ?><a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/' . $taskId)) ?>"><?= e($name) ?></a><?php endforeach ?></div>
                            <?php endif ?></td>
                        <td><?= e(in_array($method, ['lvm', 'btrfs', 'zfs', 'none'], true) ? t('snapshots.method.' . $method) : $method) ?>
                            <?php if ($method === 'lvm' && $group !== null): ?>
                                <div class="note mono"><?= e($group . '/' . (string) ($v['logical_volume'] ?? '?')) ?></div>
                                <div class="note"><?= e(t('snapshots.free', ['free' => $params['free']])) ?></div>
                            <?php endif ?></td>
                        <td><?= badge(Snapshots::status($state), in_array($state, Snapshots::STATES, true) ? t('snapshots.state.' . $state) : $state) ?></td>
                        <td><?= e($text) ?>
                            <?php if ($command !== null): ?>
                                <span class="copy-row gap-top">
                                    <input type="text" id="snapshot-fix-<?= (int) $i ?>" class="mono" value="<?= e($command) ?>" readonly size="<?= strlen($command) + 1 ?>">
                                    <button type="button" class="secondary" data-copy="snapshot-fix-<?= (int) $i ?>" data-copied="<?= e(t('common.copied')) ?>"><?= icon('copy') ?><?= e(t('common.copy')) ?></button>
                                </span>
                                <div class="note"><?= e(t('snapshots.fix.disk')) ?></div>
                            <?php endif ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
    <?php if ($snapshotsAt !== null): ?>
        <p class="note"><?= e(t('snapshots.reported', ['time' => fmt_time($snapshotsAt), 'ago' => fmt_ago($snapshotsAt)])) ?></p>
    <?php endif ?>
</section>
