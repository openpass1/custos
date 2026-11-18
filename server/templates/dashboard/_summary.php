<?php
/** @var array $summary @var int $written @var array{least: ?array, soonest: ?array} $storage */
?>
<div class="tiles">
    <a class="tile" href="<?= e(url('/agents')) ?>">
        <span class="tile-label"><?= e(t('dashboard.machines')) ?></span>
        <span class="tile-icon"><?= icon('desktop') ?></span>
        <span class="tile-value"><?= (int) $summary['online'] ?> <small>/ <?= (int) $summary['agents'] ?></small></span>
        <span class="tile-note"><?= e(t('dashboard.machines.online')) ?><?php if ($summary['pending'] > 0): ?>
            · <span class="info-text"><?= e(t('dashboard.machines.pending', ['n' => $summary['pending']])) ?></span><?php endif ?></span>
    </a>
    <a class="tile" href="<?= e(url('/tasks')) ?>">
        <span class="tile-label"><?= e(t('dashboard.tasks')) ?></span>
        <span class="tile-icon<?= (int) $summary['error'] + (int) $summary['missed'] > 0 ? ' bad' : '' ?>"><?= icon('task') ?></span>
        <?php
        // A bar of the task states with its legend (the colours say nothing without words). Widths are SVG
        // attributes: the page allows no inline styles.
        $segments = ['ok' => (int) $summary['ok'], 'warn' => (int) $summary['warning'], 'bad' => (int) $summary['error'] + (int) $summary['missed'],
                  'info' => (int) $summary['running']];
        $total = (int) $summary['tasks'];
        $segments['muted'] = max(0, $total - array_sum($segments));
        $legend = ['ok' => t('dashboard.tasks.ok'), 'warn' => t('dashboard.tasks.warning'), 'bad' => t('dashboard.tasks.bad'),
                   'info' => t('dashboard.tasks.running'), 'muted' => t('dashboard.tasks.other')];
        $x = 0.0;
        ?>
        <span class="tile-value"><?= $total ?></span>
        <?php if ($total > 0): ?>
            <svg class="seg-bar" viewBox="0 0 100 6" preserveAspectRatio="none" aria-hidden="true">
                <?php foreach ($segments as $kind => $n): ?>
                    <?php if ($n > 0): ?>
                        <?php $width = 100 * $n / $total; ?>
                        <rect class="seg-<?= $kind ?>" x="<?= round($x, 3) ?>" y="0" width="<?= round(max(0.0, $width - ($x + $width < 99.99 ? 0.8 : 0)), 3) ?>" height="6"/>
                        <?php $x += $width; ?>
                    <?php endif ?>
                <?php endforeach ?>
            </svg>
        <?php endif ?>
        <span class="tile-legend" title="<?= e(t('dashboard.tasks.note', [
            'ok' => $summary['ok'], 'warning' => $summary['warning'], 'error' => $summary['error'],
            'missed' => $summary['missed'], 'running' => $summary['running'],
        ])) ?>">
            <?php foreach ($segments as $kind => $n): ?>
                <?php if ($n > 0 || $kind === 'ok'): ?>
                    <span><i class="dot dot-<?= $kind ?>"></i><b><?= $n ?></b> <?= e($legend[$kind]) ?></span>
                <?php endif ?>
            <?php endforeach ?>
        </span>
    </a>
    <?php $least = $storage['least']; $soonest = $storage['soonest']; ?>
    <?php // The machine page with that destination in view (its storage block, _storage.php, gives each one an id). ?>
    <a class="tile" href="<?= e($least === null ? url('/agents') : url('/agents/' . $least['agent_id']) . '#' . storage_anchor($least)) ?>">
        <span class="tile-label"><?= e(t('dashboard.storage')) ?></span>
        <span class="tile-icon<?= $least !== null && $least['soon'] ? ' bad' : '' ?>"><?= icon('hdd') ?></span>
        <?php if ($least === null): ?>
            <span class="tile-value">—</span>
            <span class="tile-note"><?= e(t('dashboard.storage.none')) ?></span>
        <?php else: ?>
            <span class="tile-value"><?= e(fmt_bytes($least['free_bytes'])) ?></span>
            <span class="tile-note mono" title="<?= e(\Custos\Domain\Agents::name($least)) ?>"><?= e(t('dashboard.storage.where', [
                'path' => $least['path'] ?? $least['kind'], 'machine' => \Custos\Domain\Agents::name($least),
            ])) ?></span>
            <span class="tile-note<?= $least['soon'] ? ' bad-text' : '' ?>"><?= e(forecast_text($least)) ?></span>
            <?php if ($soonest !== null && ($soonest['agent_id'] . $soonest['task_id'] . $soonest['destination_id']) !== ($least['agent_id'] . $least['task_id'] . $least['destination_id'])): ?>
                <span class="tile-note mono" title="<?= e(\Custos\Domain\Agents::name($soonest)) ?>"><?= e(t('dashboard.storage.soonest', [
                    'path' => $soonest['path'] ?? $soonest['kind'], 'machine' => \Custos\Domain\Agents::name($soonest),
                    'forecast' => forecast_text($soonest),
                ])) ?></span>
            <?php endif ?>
        <?php endif ?>
    </a>
    <?php // The runs of the same 24 hours; the run list sums up what they wrote. ?>
    <a class="tile" href="<?= e(url('/runs', ['hours' => 24])) ?>">
        <span class="tile-label"><?= e(t('dashboard.written')) ?></span>
        <span class="tile-icon"><?= icon('cloud-upload') ?></span>
        <span class="tile-value"><?= e(fmt_bytes($written)) ?></span>
        <span class="tile-note"><?= e(t('dashboard.written.note')) ?></span>
    </a>
</div>
