<?php
/** @var list<array> $storages destinations of the machine (Storage::forAgent) */
?>
<section class="panel">
    <h2><?= e(t('storage.title')) ?></h2>
    <p class="hint"><?= e(t('storage.hint')) ?></p>
    <?php foreach ($storages as $s): ?>
        <?php
        $total = $s['total_bytes'] === null ? null : (int) $s['total_bytes'];
        $free = $s['free_bytes'] === null ? null : (int) $s['free_bytes'];
        $used = $total !== null && $free !== null && $total > 0 ? (int) round(100 * ($total - $free) / $total) : null;
        $soon = ($s['forecast']['state'] ?? '') === 'filling' && (int) $s['forecast']['days_left'] <= 14;
        ?>
        <div class="storage<?= $s['bad_sets'] > 0 ? ' storage-bad' : '' ?>">
            <div class="storage-head">
                <strong class="mono"><?= e($s['path'] ?? $s['kind']) ?></strong>
                <span class="note"><?= e(t('storage.of_task', ['task' => $s['task_name'], 'kind' => $s['kind']])) ?></span>
            </div>
            <?php if ($free !== null): ?>
                <div class="storage-space">
                    <?php if ($used !== null): ?><meter min="0" max="100" low="80" high="90" optimum="0" value="<?= $used ?>"><?= $used ?> %</meter><?php endif ?>
                    <span><?= e($total === null ? t('storage.free', ['free' => fmt_bytes($free)]) : t('storage.free_of', ['free' => fmt_bytes($free), 'total' => fmt_bytes($total)])) ?></span>
                    <span class="<?= $soon ? 'bad-text' : 'note' ?>">· <?= e(forecast_text($s['forecast'])) ?></span>
                </div>
            <?php else: ?>
                <div class="note"><?= e(t('storage.no_space')) ?></div>
            <?php endif ?>
            <div class="note"><?= e(t('storage.reported', ['time' => fmt_time($s['recorded_at']), 'ago' => fmt_ago($s['recorded_at'])])) ?></div>
            <?php if ($s['sets'] === []): ?>
                <p class="note"><?= e(t('storage.sets.none')) ?></p>
            <?php else: ?>
                <details<?= $s['bad_sets'] > 0 ? ' open' : '' ?>>
                    <summary><?= e(t('storage.sets', ['n' => count($s['sets'])])) ?><?php if ($s['bad_sets'] > 0): ?> · <span class="bad-text"><?= e(t('storage.sets.bad', ['n' => $s['bad_sets']])) ?></span><?php endif ?></summary>
                    <div class="table-wrap">
                        <table class="compact">
                            <thead><tr>
                                <th><?= e(t('storage.set.name')) ?></th><th><?= e(t('storage.set.type')) ?></th><th><?= e(t('storage.set.time')) ?></th>
                                <th class="num"><?= e(t('storage.set.size')) ?></th><th><?= e(t('storage.set.verified')) ?></th>
                            </tr></thead>
                            <tbody>
                            <?php foreach ($s['sets'] as $set): ?>
                                <?php $failed = $set['verify_ok'] !== null && (int) $set['verify_ok'] === 0; ?>
                                <tr<?= $failed ? ' class="bad-row"' : '' ?>>
                                    <td class="mono"><?= e($set['name']) ?></td>
                                    <td><?= e($set['set_type'] === null ? '—' : (t('backup.' . $set['set_type']) === 'backup.' . $set['set_type'] ? $set['set_type'] : t('backup.' . $set['set_type']))) ?></td>
                                    <td class="nowrap"><?= e(fmt_time($set['set_time'])) ?></td>
                                    <td class="num"><?= e(fmt_bytes($set['bytes'])) ?></td>
                                    <td><?php if ($set['verified_at'] === null): ?><span class="note"><?= e(t('storage.set.not_verified')) ?></span>
                                        <?php else: ?><?= badge($failed ? 'error' : 'success', t($failed ? 'storage.set.failed' : 'storage.set.ok')) ?>
                                            <span class="note"><?= e(fmt_time($set['verified_at'])) ?></span><?php endif ?></td>
                                </tr>
                            <?php endforeach ?>
                            </tbody>
                        </table>
                    </div>
                </details>
            <?php endif ?>
        </div>
    <?php endforeach ?>
</section>
