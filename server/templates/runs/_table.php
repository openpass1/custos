<?php
/** @var array $runs @var bool $showAgent */
use Custos\Domain\Agents;
?>
<div class="table-wrap">
    <table>
        <thead>
        <tr>
            <th><?= e(t('runs.col.started')) ?></th>
            <?php if ($showAgent): ?><th><?= e(t('runs.col.agent')) ?></th><?php endif ?>
            <th><?= e(t('runs.col.task')) ?></th>
            <th><?= e(t('runs.col.status')) ?></th>
            <th><?= e(t('runs.col.type')) ?></th>
            <th><?= e(t('runs.col.trigger')) ?></th>
            <th class="num"><?= e(t('runs.col.duration')) ?></th>
            <th class="num"><?= e(t('runs.col.files')) ?></th>
            <th class="num"><?= e(t('runs.col.written')) ?></th>
        </tr>
        </thead>
        <tbody>
        <?php foreach ($runs as $r): ?>
            <tr>
                <td><a href="<?= e(url('/runs/' . $r['id'])) ?>"><?= e(fmt_time($r['started_at'])) ?></a></td>
                <?php if ($showAgent): ?>
                    <td><a href="<?= e(url('/agents/' . $r['agent_id'])) ?>"><?= e(Agents::name($r)) ?></a></td>
                <?php endif ?>
                <td><a href="<?= e(url('/agents/' . $r['agent_id'] . '/tasks/' . $r['task_id'])) ?>"><?= e($r['task_name'] ?? t('runs.unknown_task')) ?></a></td>
                <td>
                    <?= badge($r['status']) ?>
                    <?php if (($r['anomaly'] ?? null) !== null): ?><span title="<?= e(anomaly_text($r['anomaly'])) ?>"><?= badge('anomaly') ?></span><?php endif ?>
                    <?php if ((int) $r['errors'] > 0): ?><span class="note"><?= e(t('runs.errors', ['n' => $r['errors']])) ?></span><?php endif ?>
                    <?php if ((int) $r['warnings'] > 0): ?><span class="note"><?= e(t('runs.warnings', ['n' => $r['warnings']])) ?></span><?php endif ?>
                </td>
                <td><?= e($r['backup_type'] === null ? '—' : t('backup.' . $r['backup_type'])) ?></td>
                <td><?= e($r['trigger_type'] === null ? '—' : t('trigger.' . $r['trigger_type'])) ?></td>
                <td class="num"><?= e(fmt_duration($r['duration_s'])) ?></td>
                <td class="num"><?= e(fmt_number($r['files_processed'])) ?></td>
                <td class="num"><?= e(fmt_bytes($r['bytes_written'])) ?></td>
            </tr>
        <?php endforeach ?>
        </tbody>
    </table>
</div>
