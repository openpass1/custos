<?php
/** @var array<string, string> $parts @var int $refresh */
?>
<h1><?= e(t('dashboard.title')) ?></h1>
<div data-live="<?= e(url('/dashboard/live')) ?>" data-live-every="<?= (int) $refresh ?>">
    <div data-part="summary"><?= $parts['summary'] ?></div>
    <div class="dashboard-grid">
        <div>
            <div data-part="attention"><?= $parts['attention'] ?></div>
        </div>
        <div>
            <div data-part="running"><?= $parts['running'] ?></div>
            <div data-part="notices"><?= $parts['notices'] ?></div>
            <div data-part="versions"><?= $parts['versions'] ?></div>
        </div>
    </div>
</div>
