<?php
/**
 * @var array $runs @var array $filter @var ?array $task @var array $agents @var int $page @var int $pages @var int $total
 * @var int $written bytes written by all runs of the filter
 */
use Custos\Domain\Agents;

$statuses = ['success', 'warning', 'error', 'aborted', 'running'];
?>
<h1><?= e(t('runs.title')) ?><?php if ($task !== null): ?> · <?= e($task['name']) ?><?php endif ?><?php if ($filter['hours'] !== ''): ?> · <?= e(t('runs.filter.hours', ['n' => (int) $filter['hours']])) ?><?php endif ?></h1>

<form method="get" action="<?= e(url('/runs')) ?>" class="filters">
    <?php if ($filter['task'] !== ''): ?><input type="hidden" name="task" value="<?= e($filter['task']) ?>"><?php endif ?>
    <?php if ($filter['hours'] !== ''): ?><input type="hidden" name="hours" value="<?= e($filter['hours']) ?>"><?php endif ?>
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
    <button type="submit" class="secondary"><?= e(t('common.apply')) ?></button>
    <?php if (array_filter($filter) !== []): ?><a href="<?= e(url('/runs')) ?>"><?= e(t('common.reset')) ?></a><?php endif ?>
    <a class="button-link" href="<?= e(url('/runs/export', $filter)) ?>" title="<?= e(t('runs.export.hint')) ?>"><?= icon('download') ?><?= e(t('runs.export')) ?></a>
</form>

<?php /* Live part: new runs and the end of a running one show up by themselves (app.js); the filter stays as chosen. */ ?>
<div data-part="list">
<?php if ($runs === []): ?>
    <p class="empty"><?= e(t('runs.empty')) ?></p>
<?php else: ?>
    <section class="panel">
        <p class="note"><?= e(t('runs.total', ['n' => $total])) ?><?php if ($written > 0): ?> · <?= e(t('runs.total.written', ['bytes' => fmt_bytes($written)])) ?><?php endif ?></p>
        <?php $showAgent = true; include __DIR__ . '/_table.php'; ?>
    </section>
    <?= pager($page, $pages, '/runs', $filter) ?>
<?php endif ?>
</div>
