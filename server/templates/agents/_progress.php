<?php
/** @var array{agent: array, task: ?array, progress: ?array} $item a run in progress */
$agent = $item['agent'];
$task = $item['task'];
$progress = $item['progress'];
$paused = $agent['state'] === 'paused' || $agent['paused_at'] !== null;
$percent = $progress === null ? null : min(100, (float) $progress['percent']);
?>
<?php // The machine and the task, what it does now, the bar with the percentage, speed and time left, the current file. ?>
<div class="progress-item<?= $paused ? ' paused' : '' ?>" data-key="r<?= e($agent['id']) ?>">
    <span class="lead lead-info"><?= icon($paused ? 'pause' : 'refresh') ?></span>
    <div class="progress-body">
        <div class="progress-title">
            <span><a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e($agent['name']) ?></a> ·
            <?php if ($task !== null): ?>
                <a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/' . $task['id'])) ?>"><?= e($task['name']) ?></a>
            <?php else: ?>
                <?= e(t('runs.unknown_task')) ?>
            <?php endif ?></span>
            <?php if ($paused): ?><?= badge('paused') ?><?php endif ?>
        </div>
        <?php if ($progress !== null): ?>
            <div class="progress-sub"><?= e(t('phase.' . $progress['phase'])) ?>
                · <?= e(fmt_number($progress['files_done'])) ?> / <?= e(fmt_number($progress['files_total'])) ?> <?= e(t('agent.files')) ?>
                <?php if ($paused && $agent['paused_at'] !== null): ?>· <?= e(t('agent.paused_since', ['time' => fmt_time($agent['paused_at'])])) ?><?php endif ?></div>
            <div class="progress-bar">
                <progress<?= $paused ? '' : ' class="live"' ?> max="100" value="<?= e($percent) ?>"></progress>
                <span class="percent"><?= (int) floor($percent) ?>%</span>
            </div>
            <?php if (!$paused): ?>
                <div class="note"><?= e(fmt_bytes($progress['speed_bps'])) ?>/<?= e(t('unit.s')) ?> · <?= e(t('agent.eta', ['time' => fmt_duration($progress['eta_s'])])) ?></div>
            <?php endif ?>
            <?php if ($progress['current_file'] !== null): ?><div class="note mono ellipsis" title="<?= e($progress['current_file']) ?>"><?= e($progress['current_file']) ?></div><?php endif ?>
        <?php else: ?>
            <div class="progress-sub"><?= e(t('agent.progress.waiting')) ?></div>
        <?php endif ?>
    </div>
</div>
