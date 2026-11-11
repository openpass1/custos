<?php
/** @var array $runs @var array $filter @var ?array $task @var array $agents @var int $page @var int $pages @var int $total */
use Custos\Domain\Agents;

$statuses = ['success', 'warning', 'error', 'aborted', 'running'];
?>
<h1><?= e(t('runs.title')) ?><?php if ($task !== null): ?> · <?= e($task['name']) ?><?php endif ?></h1>

<form method="get" action="<?= e(url('/runs')) ?>" class="filters">
    <?php if ($filter['task'] !== ''): ?><input type="hidden" name="task" value="<?= e($filter['task']) ?>"><?php endif ?>
    <label><?= e(t('runs.filter.agent')) ?>
        <select name="agent">
            <option value=""><?= e(t('common.all')) ?></option>
            <?php foreach ($agents as $a): ?>
                <option value="<?= e($a['id']) ?>"<?= $filter['agent'] === $a['id'] ? ' selected' : '' ?>><?= e(Agents::name($a)) ?></option>
            <?php endforeach ?>
        </select>
    </label>
    <label><?= e(t('runs.filter.status')) ?>
        <select name="status">
            <option value=""><?= e(t('common.all')) ?></option>
            <?php foreach ($statuses as $s): ?>
                <option value="<?= e($s) ?>"<?= $filter['status'] === $s ? ' selected' : '' ?>><?= e(t('status.' . $s)) ?></option>
            <?php endforeach ?>
        </select>
    </label>
    <label><?= e(t('common.from')) ?> <input type="date" name="from" value="<?= e($filter['from']) ?>"></label>
    <label><?= e(t('common.to')) ?> <input type="date" name="to" value="<?= e($filter['to']) ?>"></label>
    <button type="submit"><?= e(t('common.apply')) ?></button>
    <?php if (array_filter($filter) !== []): ?><a href="<?= e(url('/runs')) ?>"><?= e(t('common.reset')) ?></a><?php endif ?>
    <a class="button-link" href="<?= e(url('/runs/export', $filter)) ?>" title="<?= e(t('runs.export.hint')) ?>"><?= e(t('runs.export')) ?></a>
</form>

<?php if ($runs === []): ?>
    <p class="empty"><?= e(t('runs.empty')) ?></p>
<?php else: ?>
    <p class="note"><?= e(t('runs.total', ['n' => $total])) ?></p>
    <?php $showAgent = true; include __DIR__ . '/_table.php'; ?>
    <?= pager($page, $pages, '/runs', $filter) ?>
<?php endif ?>
