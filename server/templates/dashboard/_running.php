<?php
/** @var list<array> $running */
?>
<section class="panel">
    <h2><?= e(t('dashboard.running')) ?></h2>
    <?php if ($running === []): ?>
        <p class="empty"><?= e(t('dashboard.running.none')) ?></p>
    <?php else: ?>
        <?php foreach ($running as $item): ?>
            <?php include __DIR__ . '/../agents/_progress.php' ?>
        <?php endforeach ?>
    <?php endif ?>
</section>
