<?php
/** @var array<string, string> $parts @var int $refresh @var array $heatmap */
?>
<h1><?= e(t('dashboard.title')) ?></h1>
<div data-live="<?= e(url('/dashboard/live')) ?>" data-live-every="<?= (int) $refresh ?>">
    <div data-part="summary"><?= $parts['summary'] ?></div>
    <?php /* Left: what needs attention and the 30 days at a glance; right: what runs now, notices and versions. */ ?>
    <div class="dashboard-grid">
        <div class="dash-main" data-part="attention"><?= $parts['attention'] ?></div>
        <div class="dash-heat"><?php include __DIR__ . '/_heatmap.php' ?></div>
        <div class="dash-side">
            <div data-part="running"><?= $parts['running'] ?></div>
            <div data-part="notices"><?= $parts['notices'] ?></div>
            <div data-part="versions"><?= $parts['versions'] ?></div>
        </div>
    </div>
</div>
