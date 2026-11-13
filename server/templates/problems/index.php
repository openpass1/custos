<?php
/** @var list<array> $open @var list<array> $closed @var int $page @var int $pages */
use Custos\Domain\Agents;

$row = function (array $p, bool $isOpen): string {
    ob_start(); ?>
    <tr>
        <td><?= badge($isOpen ? $p['kind'] : 'closed', t('problem.kind.' . $p['kind'])) ?></td>
        <td class="nowrap"><a href="<?= e(url('/agents/' . $p['agent_id'])) ?>"><?= e(Agents::name($p)) ?></a></td>
        <td><a href="<?= e(url('/agents/' . $p['agent_id'] . '/tasks/' . $p['task_id'])) ?>"><?= e($p['task_name'] ?? t('runs.unknown_task')) ?></a></td>
        <td><?= e(problem_text($p)) ?></td>
        <td class="nowrap"><?= e(fmt_time($p['opened_at'])) ?></td>
        <td><?= $isOpen ? e(fmt_ago($p['opened_at'])) : e(fmt_time($p['closed_at']) . ' · ' . t('closed_by.' . $p['closed_by'])) ?></td>
        <td class="note"><?= e(t('notify.state.' . $p['notify'])) ?></td>
    </tr>
    <?php return (string) ob_get_clean();
};
$head = '<thead><tr><th>' . e(t('problems.col.kind')) . '</th><th>' . e(t('problems.col.agent')) . '</th><th>'
    . e(t('problems.col.task')) . '</th><th>' . e(t('problems.col.what')) . '</th><th>' . e(t('problems.col.opened')) . '</th><th>%s</th><th>'
    . e(t('problems.col.notify')) . '</th></tr></thead>';
?>
<h1><?= e(t('problems.title')) ?></h1>
<p class="hint"><?= e(t('problems.hint')) ?></p>

<section class="panel<?= $open === [] ? ' panel-empty' : '' ?>">
    <h2><?= e(t('problems.open.title')) ?></h2>
    <?php if ($open === []): ?>
        <p class="all-good"><?= e(t('dashboard.no_problems')) ?></p>
    <?php else: ?>
        <div class="table-wrap"><table><?= sprintf($head, e(t('problems.col.duration'))) ?><tbody>
            <?php foreach ($open as $p): ?><?= $row($p, true) ?><?php endforeach ?>
        </tbody></table></div>
    <?php endif ?>
</section>

<section class="panel<?= $closed === [] ? ' panel-empty' : '' ?>">
    <h2><?= e(t('problems.closed.title')) ?></h2>
    <?php if ($closed === []): ?>
        <p class="empty"><?= e(t('problems.closed.none')) ?></p>
    <?php else: ?>
        <div class="table-wrap"><table><?= sprintf($head, e(t('problems.col.closed'))) ?><tbody>
            <?php foreach ($closed as $p): ?><?= $row($p, false) ?><?php endforeach ?>
        </tbody></table></div>
        <?= pager($page, $pages, '/problems', []) ?>
    <?php endif ?>
</section>
