<?php
/** @var string $q @var list<string> $words @var list<array> $machines @var list<array> $tasks */
?>
<h1><?= e($q === '' ? t('search.title') : t('search.title.q', ['q' => $q])) ?></h1>
<?php if ($q === ''): ?>
    <p class="hint"><?= e(t('search.hint')) ?></p>
<?php else: ?>
    <section class="panel search-list">
        <?php $suggest = false; include __DIR__ . '/_list.php' ?>
    </section>
    <p class="hint"><?= e(t('search.hint')) ?></p>
<?php endif ?>
