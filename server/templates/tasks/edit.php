<?php
/**
 * @var array $agent @var array $task @var list<array> $errors @var ?array $pending @var bool $isNew @var bool $readOnly
 * @var ?string $refusal @var array $credentials @var string $action @var ?string $base
 */
use Custos\Domain\Agents;

$taskUrl = $isNew ? null : '/agents/' . $agent['id'] . '/tasks/' . $task['id'];
?>
<p class="crumbs"><a href="<?= e(url('/agents')) ?>"><?= e(t('agents.title')) ?></a> ›
    <a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e(Agents::name($agent)) ?></a>
    <?php if ($taskUrl !== null): ?>› <a href="<?= e(url($taskUrl)) ?>"><?= e((string) ($task['name'] ?? '')) ?></a><?php endif ?></p>
<h1><?= e($isNew ? t('task_form.title.new') : t('task_form.title.edit', ['name' => (string) ($task['name'] ?? '')])) ?></h1>

<?php if ($refusal !== null): ?>
    <div class="alert alert-warn"><?= e($refusal) ?> <?= e(t('task_form.read_only')) ?></div>
<?php endif ?>
<?php if ($pending !== null): ?>
    <div class="alert alert-info"><?= e(t('task_form.pending', ['time' => fmt_time($pending['created_at']), 'by' => (string) $pending['created_by_login']])) ?>
        <?= badge($pending['status']) ?></div>
<?php endif ?>
<p class="hint"><?= e(t('task_form.hint')) ?></p>

<form method="post" action="<?= e(url($action)) ?>" class="editor-form" autocomplete="off">
    <?= csrf_field() ?>
    <?php if ($base !== null): ?><input type="hidden" name="_base" value="<?= e($base) ?>"><?php endif ?>
    <?php $templateMode = false; include __DIR__ . '/_form.php'; ?>
    <?php if (!$readOnly): ?>
        <div class="buttons sticky-buttons">
            <button type="submit"><?= e(t($isNew ? 'task_form.submit.create' : 'task_form.submit.save')) ?></button>
            <a href="<?= e(url($taskUrl ?? '/agents/' . $agent['id'])) ?>"><?= e(t('task_form.cancel')) ?></a>
            <span class="note"><?= e(t('task_form.submit.hint')) ?></span>
        </div>
    <?php endif ?>
</form>
