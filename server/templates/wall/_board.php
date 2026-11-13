<?php
/**
 * @var \Custos\Domain\Board $board @var array $summary @var array $soft @var list<array> $running @var bool $cronLate
 * @var string $updated
 */
$bad = (int) $summary['error'] + (int) $summary['missed'];
$softCount = count($soft['offline']) + count($soft['stale']) + count($soft['anomaly']);
?>
<p class="wall-updated"><?= e(t('wall.updated', ['time' => fmt_time($updated, true)])) ?></p>

<?php if ($cronLate): ?>
    <div class="wall-alert"><?= e(t('wall.cron_late')) ?></div>
<?php endif ?>

<?php if ($board->problems === []): ?>
    <div class="wall-ok"><?= e(t('wall.all_good')) ?></div>
<?php else: ?>
    <div class="wall-problems">
        <?php foreach ($board->problems as $p): ?>
            <div class="wall-problem wall-problem-<?= e($p['kind']) ?>" data-key="p<?= (int) $p['id'] ?>">
                <div class="wall-problem-head"><?= e($p['agent']['name']) ?> · <?= e($p['task_name'] ?? t('runs.unknown_task')) ?></div>
                <div class="wall-problem-text"><?= e(problem_text($p)) ?></div>
                <div class="wall-problem-since"><?= e(t('problem.since', ['time' => fmt_time($p['opened_at']), 'ago' => fmt_ago($p['opened_at'])])) ?></div>
            </div>
        <?php endforeach ?>
    </div>
<?php endif ?>

<div class="wall-tiles">
    <div class="wall-tile">
        <span class="wall-tile-value"><?= (int) $summary['online'] ?><small> / <?= (int) $summary['agents'] ?></small></span>
        <span class="wall-tile-label"><?= e(t('wall.online')) ?></span>
    </div>
    <div class="wall-tile wall-tile-ok">
        <span class="wall-tile-value"><?= (int) $summary['ok'] ?></span>
        <span class="wall-tile-label"><?= e(t('status.ok')) ?></span>
    </div>
    <div class="wall-tile<?= (int) $summary['warning'] > 0 ? ' wall-tile-warn' : '' ?>">
        <span class="wall-tile-value"><?= (int) $summary['warning'] ?></span>
        <span class="wall-tile-label"><?= e(t('wall.warnings')) ?></span>
    </div>
    <div class="wall-tile<?= $bad > 0 ? ' wall-tile-bad' : '' ?>">
        <span class="wall-tile-value"><?= $bad ?></span>
        <span class="wall-tile-label"><?= e(t('wall.problems')) ?></span>
    </div>
    <div class="wall-tile<?= (int) $summary['running'] > 0 ? ' wall-tile-info' : '' ?>">
        <span class="wall-tile-value"><?= (int) $summary['running'] ?></span>
        <span class="wall-tile-label"><?= e(t('wall.running')) ?></span>
    </div>
</div>

<?php if ($running !== []): ?>
    <div class="wall-running">
        <?php foreach ($running as $item): ?>
            <?php $progress = $item['progress']; $paused = $item['agent']['state'] === 'paused' || $item['agent']['paused_at'] !== null; ?>
            <div class="wall-run" data-key="r<?= e($item['agent']['id']) ?>">
                <div class="wall-run-head">
                    <?= e($item['agent']['name']) ?> · <?= e($item['task']['name'] ?? t('runs.unknown_task')) ?>
                    <span class="wall-run-state"><?php if ($paused): ?><?= e(t('status.paused')) ?><?php elseif ($progress !== null): ?><?= e(round((float) $progress['percent'])) ?>&nbsp;%<?php if ($progress['eta_s'] !== null): ?> · <?= e(t('agent.eta', ['time' => fmt_duration($progress['eta_s'])])) ?><?php endif ?><?php endif ?></span>
                </div>
                <progress max="100" value="<?= e($progress === null ? 0 : min(100, (float) $progress['percent'])) ?>"></progress>
            </div>
        <?php endforeach ?>
    </div>
<?php endif ?>

<?php if ($softCount > 0): ?>
    <div class="wall-soft">
        <?php if ($soft['offline'] !== []): ?>
            <div><span class="wall-soft-label"><?= e(t('dashboard.soft.offline')) ?>:</span>
                <?= e(implode(', ', array_map(fn($i) => $i['agent']['name'], $soft['offline']))) ?></div>
        <?php endif ?>
        <?php if ($soft['stale'] !== []): ?>
            <div><span class="wall-soft-label"><?= e(t('dashboard.soft.stale')) ?>:</span>
                <?= e(implode(', ', array_map(fn($task) => $task['agent']['name'] . ' · ' . $task['name'], $soft['stale']))) ?></div>
        <?php endif ?>
        <?php if ($soft['anomaly'] !== []): ?>
            <div><span class="wall-soft-label"><?= e(t('dashboard.soft.anomaly')) ?>:</span>
                <?= e(implode(', ', array_map(fn($task) => $task['agent']['name'] . ' · ' . $task['name'], $soft['anomaly']))) ?></div>
        <?php endif ?>
    </div>
<?php endif ?>
