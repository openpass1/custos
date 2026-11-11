<?php
/** @var array{agent: array, task: ?array, progress: ?array} $item a run in progress */
$agent = $item['agent'];
$task = $item['task'];
$progress = $item['progress'];
$paused = $agent['state'] === 'paused' || $agent['paused_at'] !== null;
?>
<div class="progress-item">
    <div>
        <?= badge($paused ? 'paused' : 'running') ?>
        <a href="<?= e(url('/agents/' . $agent['id'])) ?>"><strong><?= e($agent['name']) ?></strong></a> ·
        <?php if ($task !== null): ?>
            <a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/' . $task['id'])) ?>"><?= e($task['name']) ?></a>
        <?php else: ?>
            <?= e(t('runs.unknown_task')) ?>
        <?php endif ?>
        <?php if ($paused && $agent['paused_at'] !== null): ?>
            <span class="note"><?= e(t('agent.paused_since', ['time' => fmt_time($agent['paused_at'])])) ?></span>
        <?php endif ?>
    </div>
    <?php if ($progress !== null): ?>
        <progress max="100" value="<?= e(min(100, (float) $progress['percent'])) ?>"></progress>
        <div class="note">
            <?= e(t('phase.' . $progress['phase'])) ?> · <?= e(round((float) $progress['percent'], 1)) ?>%
            · <?= e(fmt_number($progress['files_done'])) ?> / <?= e(fmt_number($progress['files_total'])) ?> <?= e(t('agent.files')) ?>
            <?php if (!$paused): ?>
                · <?= e(fmt_bytes($progress['speed_bps'])) ?>/<?= e(t('unit.s')) ?>
                · <?= e(t('agent.eta', ['time' => fmt_duration($progress['eta_s'])])) ?>
            <?php endif ?>
        </div>
        <?php if ($progress['current_file'] !== null): ?><div class="note mono ellipsis" title="<?= e($progress['current_file']) ?>"><?= e($progress['current_file']) ?></div><?php endif ?>
    <?php else: ?>
        <div class="note"><?= e(t('agent.progress.waiting')) ?></div>
    <?php endif ?>
</div>
