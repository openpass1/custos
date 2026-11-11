<?php
/** @var array $summary @var int $written @var array{least: ?array, soonest: ?array} $storage */
?>
<div class="tiles">
    <a class="tile" href="<?= e(url('/agents')) ?>">
        <span class="tile-label"><?= e(t('dashboard.machines')) ?></span>
        <span class="tile-value"><?= (int) $summary['online'] ?> <small>/ <?= (int) $summary['agents'] ?></small></span>
        <span class="tile-note"><?= e(t('dashboard.machines.online')) ?><?php if ($summary['pending'] > 0): ?>
            · <span class="info-text"><?= e(t('dashboard.machines.pending', ['n' => $summary['pending']])) ?></span><?php endif ?></span>
    </a>
    <a class="tile" href="<?= e(url('/agents')) ?>">
        <span class="tile-label"><?= e(t('dashboard.tasks')) ?></span>
        <span class="tile-counts">
            <span class="dot dot-ok"></span><?= (int) $summary['ok'] ?>
            <span class="dot dot-warn"></span><?= (int) $summary['warning'] ?>
            <span class="dot dot-bad"></span><?= (int) $summary['error'] + (int) $summary['missed'] ?>
            <span class="dot dot-info"></span><?= (int) $summary['running'] ?>
        </span>
        <span class="tile-note"><?= e(t('dashboard.tasks.note', [
            'ok' => $summary['ok'], 'warning' => $summary['warning'], 'error' => $summary['error'],
            'missed' => $summary['missed'], 'running' => $summary['running'],
        ])) ?></span>
    </a>
    <?php $least = $storage['least']; $soonest = $storage['soonest']; ?>
    <a class="tile" href="<?= e(url($least === null ? '/agents' : '/agents/' . $least['agent_id'])) ?>">
        <span class="tile-label"><?= e(t('dashboard.storage')) ?></span>
        <?php if ($least === null): ?>
            <span class="tile-value">—</span>
            <span class="tile-note"><?= e(t('dashboard.storage.none')) ?></span>
        <?php else: ?>
            <span class="tile-value"><?= e(fmt_bytes($least['free_bytes'])) ?></span>
            <span class="tile-note mono" title="<?= e(\Custos\Domain\Agents::name($least)) ?>"><?= e(t('dashboard.storage.where', [
                'path' => $least['path'] ?? $least['kind'], 'machine' => \Custos\Domain\Agents::name($least),
            ])) ?></span>
            <span class="tile-note<?= $least['state'] === 'filling' && (int) $least['days_left'] <= 14 ? ' bad-text' : '' ?>"><?= e(forecast_text($least)) ?></span>
            <?php if ($soonest !== null && ($soonest['agent_id'] . $soonest['task_id'] . $soonest['destination_id']) !== ($least['agent_id'] . $least['task_id'] . $least['destination_id'])): ?>
                <span class="tile-note mono" title="<?= e(\Custos\Domain\Agents::name($soonest)) ?>"><?= e(t('dashboard.storage.soonest', [
                    'path' => $soonest['path'] ?? $soonest['kind'], 'machine' => \Custos\Domain\Agents::name($soonest),
                    'forecast' => forecast_text($soonest),
                ])) ?></span>
            <?php endif ?>
        <?php endif ?>
    </a>
    <a class="tile" href="<?= e(url('/runs')) ?>">
        <span class="tile-label"><?= e(t('dashboard.written')) ?></span>
        <span class="tile-value"><?= e(fmt_bytes($written)) ?></span>
        <span class="tile-note"><?= e(t('dashboard.written.note')) ?></span>
    </a>
</div>
