<?php
/**
 * @var ?array $tpl @var array $task @var array $credentials @var list<array> $errors @var bool $isNew @var string $action
 * @var ?string $base @var ?string $credentialsField
 */
?>
<p class="crumbs"><a href="<?= e(url('/templates')) ?>"><?= e(t('templates.title')) ?></a>
    <?php if (!$isNew): ?>› <a href="<?= e(url('/templates/' . $tpl['id'])) ?>"><?= e($tpl['name']) ?></a><?php endif ?></p>
<h1><?= e($isNew ? t('templates.title.new') : t('templates.title.edit', ['name' => $tpl['name']])) ?></h1>
<p class="hint"><?= e(t('templates.edit.hint')) ?></p>

<form method="post" action="<?= e(url($action)) ?>" class="editor-form" autocomplete="off">
    <?= csrf_field() ?>
    <?php if ($base !== null): ?><input type="hidden" name="_base" value="<?= e($base) ?>"><?php endif ?>
    <?php if ($credentialsField !== null): ?><input type="hidden" name="_credentials" value="<?= e($credentialsField) ?>"><?php endif ?>
    <?php $templateMode = true; $readOnly = false; include __DIR__ . '/../tasks/_form.php'; ?>
    <div class="buttons sticky-buttons">
        <button type="submit"><?= e(t($isNew ? 'templates.submit.create' : 'templates.submit.save')) ?></button>
        <a href="<?= e(url($isNew ? '/templates' : '/templates/' . $tpl['id'])) ?>"><?= e(t('task_form.cancel')) ?></a>
        <?php if (!$isNew): ?><span class="note"><?= e(t('templates.submit.hint')) ?></span><?php endif ?>
    </div>
</form>
