<?php
/** @var array $summary @var int $written @var ?array $storage */
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
    <div class="tile">
        <span class="tile-label"><?= e(t('dashboard.storage')) ?></span>
        <?php if ($storage === null): ?>
            <span class="tile-value">—</span>
            <span class="tile-note"><?= e(t('dashboard.storage.none')) ?></span>
        <?php else: ?>
            <span class="tile-value"><?= e(fmt_bytes($storage['free_bytes'])) ?></span>
            <span class="tile-note mono" title="<?= e(\Custos\Domain\Agents::name($storage)) ?>"><?= e(t('dashboard.storage.where', [
                'path' => $storage['path'] ?? $storage['kind'], 'machine' => \Custos\Domain\Agents::name($storage),
            ])) ?></span>
        <?php endif ?>
    </div>
    <a class="tile" href="<?= e(url('/runs')) ?>">
        <span class="tile-label"><?= e(t('dashboard.written')) ?></span>
        <span class="tile-value"><?= e(fmt_bytes($written)) ?></span>
        <span class="tile-note"><?= e(t('dashboard.written.note')) ?></span>
    </a>
</div>
