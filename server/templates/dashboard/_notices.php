<?php
/** @var list<array> $notices @var int $unread */
?>
<section class="panel">
    <h2><?= e(t('dashboard.notices')) ?><?php if ($unread > 0): ?> <span class="count"><?= $unread ?></span><?php endif ?></h2>
    <?php if ($notices === []): ?>
        <p class="empty"><?= e(t('notices.none_unread')) ?></p>
    <?php else: ?>
        <ul class="notices">
            <?php foreach ($notices as $n): ?>
                <?php include __DIR__ . '/../notices/_item.php' ?>
            <?php endforeach ?>
        </ul>
        <form method="post" action="<?= e(url('/notices/read')) ?>" class="inline">
            <?= csrf_field() ?><input type="hidden" name="back" value="dashboard">
            <button type="submit" class="link"><?= e(t('notices.read_all')) ?></button>
            · <a href="<?= e(url('/notices')) ?>"><?= e(t('notices.all')) ?></a>
        </form>
    <?php endif ?>
</section>
