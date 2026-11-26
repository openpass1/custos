<?php
/**
 * @var array $agent from the board @var array $task from the board @var list<array> $problems @var list<array> $slots
 * @var list<array> $runs @var ?array $progress @var list<array> $commands @var ?array $pending @var ?array $templateLink
 * @var int $versionCount @var list<array> $chart
 *
 * Everything but the charts is a live part (data-part): the page refreshes it by itself (app.js), so a run, its result
 * and a change confirmed by the agent show up without reloading. The charts are history and come with the page only.
 */
$def = $task['def'];
$taskUrl = '/agents/' . $agent['id'] . '/tasks/' . $task['id'];
$isNew = !empty($task['new']);
$approved = $agent['status'] === 'approved';
$running = in_array($task['status'], ['running', 'paused'], true);
// A button that sends a command to the task, with a question first when given.
$command = function (string $action, string $class, string $iconName, ?string $confirm = null) use ($taskUrl): string {
    return '<form method="post" action="' . e(url($taskUrl . '/' . $action)) . '"' . ($confirm === null ? '' : ' data-confirm="' . e($confirm) . '"') . '>'
        . csrf_field() . '<button type="submit"' . ($class === '' ? '' : ' class="' . e($class) . '"') . '>' . icon($iconName)
        . e(t('task.action.' . $action)) . '</button></form>';
};
?>
<p class="crumbs"><a href="<?= e(url('/agents')) ?>"><?= e(t('agents.title')) ?></a> ›
    <a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e($agent['name']) ?></a></p>
<div data-part="head">
<div class="page-head">
    <h1><?= e($task['name']) ?> <?= badge($task['status']) ?>
        <?php foreach (array_diff($task['flags'], [$task['status'], 'ok', 'never', 'disabled']) as $flag): ?><?= badge($flag) ?><?php endforeach ?>
        <?php if (!empty($task['deleted'])): ?><?= badge('deleted', t('tasks.deleted')) ?><?php endif ?>
    </h1>
    <?php if ($approved && empty($task['deleted'])): ?>
        <div class="page-tools">
            <?php if (!$isNew): ?>
                <?php if ($running): ?>
                    <?= $task['status'] === 'paused' ? $command('resume', '', 'play') : $command('pause', 'secondary', 'pause') ?>
                    <?= $command('stop', 'danger', 'stop', t('task.confirm.stop')) ?>
                <?php else: ?>
                    <?= $command('run', '', 'play') ?>
                <?php endif ?>
            <?php endif ?>
            <a class="button-link" href="<?= e(url($taskUrl . '/edit')) ?>"><?= icon('pencil') ?><?= e(t('task.action.edit')) ?></a>
            <?php if (!$isNew): ?>
                <details class="dropdown">
                    <summary><?= e(t('common.more')) ?><?= icon('chevron') ?></summary>
                    <div class="dropdown-menu">
                        <?= !empty($def['enabled']) ? $command('disable', 'menu-item', 'power', t('task.confirm.disable')) : $command('enable', 'menu-item', 'power') ?>
                        <a class="menu-item" href="<?= e(url('/agents/' . $agent['id'] . '/tasks/new', ['from' => $task['id']])) ?>"><?= icon('copy') ?><?= e(t('task.action.clone')) ?></a>
                        <?php if (\Custos\Domain\Agents::canRemoteRestore($agent)): ?>
                            <a class="menu-item" href="<?= e(url($taskUrl . '/backup-restore')) ?>"><?= icon('restore') ?><?= e(t('restore.title')) ?></a>
                        <?php endif ?>
                        <a class="menu-item" href="<?= e(url('/templates/new', ['agent' => $agent['id'], 'task' => $task['id']])) ?>"><?= icon('template') ?><?= e(t('task.action.template')) ?></a>
                        <?php if ($versionCount > 0): ?>
                            <a class="menu-item" href="<?= e(url($taskUrl . '/versions')) ?>"><?= icon('clock') ?><?= e(t('task.action.history')) ?></a>
                        <?php endif ?>
                        <div class="menu-sep"></div>
                        <?= $command('delete', 'menu-item danger', 'trash', t('task.confirm.delete', ['name' => $task['name']])) ?>
                    </div>
                </details>
            <?php endif ?>
        </div>
    <?php endif ?>
    <p class="page-meta">
        <span><?= e(task_summary($def)) ?></span>
        <?php if (!$running && $progress === null && $task['next_at'] !== null): ?><span><?= e(t('task.meta.next', ['time' => fmt_unix($task['next_at'])])) ?></span><?php endif ?>
    </p>
</div>

<?php if ($pending !== null): ?>
    <div class="alert alert-info"><?= e(t($isNew ? 'task.pending.new' : 'task.pending', [
        'time' => fmt_time($pending['created_at']), 'by' => (string) $pending['created_by_login'],
    ])) ?> <?= badge($pending['status']) ?></div>
<?php endif ?>
<?php if ($templateLink !== null): ?>
    <p class="note"><?= e(t('task.template', ['name' => $templateLink['name'], 'revision' => (int) $templateLink['template_revision']])) ?>
        <?php if ((int) $templateLink['template_revision'] < (int) $templateLink['revision']): ?><?= badge('warning', t('task.template.outdated', ['revision' => (int) $templateLink['revision']])) ?><?php endif ?>
        <a href="<?= e(url('/templates/' . $templateLink['id'])) ?>"><?= e(t('task.template.open')) ?></a></p>
<?php endif ?>
</div>

<div class="columns" data-part="state">
    <section class="panel">
        <h2><?= e(t('task.state')) ?></h2>
        <dl class="props">
            <dt><?= e(t('tasks.col.schedule')) ?></dt><dd><?= e(schedule_label($def['schedule'] ?? null)) ?>
                <span class="note">(<?= e(t('task.agent_time', ['zone' => $agent['time_zone']])) ?>)</span></dd>
            <dt><?= e(t('tasks.col.next')) ?></dt><dd><?= e(fmt_unix($task['next_at'])) ?><?= $task['queued'] ? ' · ' . e(t('tasks.queued')) : '' ?></dd>
            <dt><?= e(t('tasks.col.last_run')) ?></dt>
            <dd><?php if ($task['last_run_id'] !== null): ?>
                    <a href="<?= e(url('/runs/' . $task['last_run_id'])) ?>"><?= badge($task['last_run_status']) ?></a> <?= e(fmt_time($task['last_run_at'])) ?>
                <?php else: ?><?= e(t('tasks.never')) ?><?php endif ?></dd>
            <dt><?= e(t('tasks.col.last_success')) ?></dt><dd><?= e(fmt_time($task['last_success_at'])) ?></dd>
            <dt><?= e(t('task.watched')) ?></dt>
            <dd><?= e($task['watched'] ? t('task.watched.since', ['time' => fmt_time($task['watch_from'] ?? $task['first_seen_at'])]) : t('task.watched.no')) ?></dd>
            <dt><?= e(t('tasks.col.revision')) ?></dt>
            <dd><?= (int) $task['revision'] ?> · <?= e(fmt_time($task['updated_at'])) ?> · <?= e((string) $task['updated_by']) ?>
                <?php if ($versionCount > 0): ?>· <a href="<?= e(url($taskUrl . '/versions')) ?>"><?= e(t('task.versions', ['n' => $versionCount])) ?></a><?php endif ?></dd>
        </dl>
        <p><a href="<?= e(url('/runs', ['agent' => $agent['id'], 'task' => $task['id']])) ?>"><?= e(t('task.all_runs')) ?></a></p>
    </section>

    <?php /* While the task does not run, the head says when it runs next: no panel for one line. */ ?>
    <?php if ($progress !== null || $agent['running_task'] === $task['id']): ?>
        <section class="panel">
            <h2><?= e(t('task.now')) ?></h2>
            <?php $item = ['agent' => $agent, 'task' => $task, 'progress' => $progress]; include __DIR__ . '/../agents/_progress.php'; ?>
        </section>
    <?php endif ?>
</div>

<?php if (empty($task['new'])): ?>
    <?php include __DIR__ . '/_charts.php' ?>
<?php endif ?>

<div data-part="problems">
<section class="panel<?= $problems === [] ? ' panel-empty' : '' ?>">
    <h2><?= e(t('task.problems')) ?></h2>
    <?php if ($problems === []): ?>
        <p class="empty"><?= e(t('task.problems.none')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead><tr><th><?= e(t('problems.col.kind')) ?></th><th><?= e(t('problems.col.what')) ?></th>
                    <th><?= e(t('problems.col.opened')) ?></th><th><?= e(t('problems.col.closed')) ?></th></tr></thead>
                <tbody>
                <?php foreach ($problems as $p): ?>
                    <?php $p['time_zone'] = $agent['time_zone']; ?>
                    <tr>
                        <td><?= badge($p['closed_at'] === null ? $p['kind'] : 'closed', t('problem.kind.' . $p['kind'])) ?></td>
                        <td><?= e(problem_text($p)) ?></td>
                        <td class="nowrap"><?= e(fmt_time($p['opened_at'])) ?></td>
                        <td><?= $p['closed_at'] === null ? e(t('problems.open')) : e(fmt_time($p['closed_at']) . ' · ' . t('closed_by.' . $p['closed_by'])) ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>

</div>

<div data-part="slots">
<section class="panel">
    <h2><?= e(t('task.slots', ['days' => \Custos\Http\Web\Controller\TasksController::SLOT_DAYS])) ?></h2>
    <p class="hint"><?= e(t('task.slots.hint')) ?></p>
    <?php if ($slots === []): ?>
        <p class="empty"><?= e(t('task.slots.none')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead><tr><th><?= e(t('task.slot')) ?></th><th><?= e(t('task.slot.result')) ?></th><th><?= e(t('task.slot.closed')) ?></th></tr></thead>
                <tbody>
                <?php foreach ($slots as $slot): ?>
                    <tr>
                        <td class="nowrap"><?= e(fmt_local($slot['slot_at'], $agent['time_zone'])) ?>
                            <?php if ($slot['kind'] === 'interval'): ?><span class="note"><?= e(t('task.slot.interval')) ?></span><?php endif ?></td>
                        <td><?= badge($slot['result'], t('slot.' . $slot['result'])) ?>
                            <?php if ($slot['run_id'] !== null): ?><a href="<?= e(url('/runs/' . $slot['run_id'])) ?>"><?= e(t('task.slot.run')) ?></a><?php endif ?></td>
                        <td><?php if ($slot['closed_at'] !== null): ?>
                                <?= e(fmt_time($slot['closed_at']) . ' · ' . t('closed_by.' . $slot['closed_by'])) ?>
                                <?php if ($slot['closed_run_id'] !== null): ?><a href="<?= e(url('/runs/' . $slot['closed_run_id'])) ?>"><?= e(t('task.slot.run')) ?></a><?php endif ?>
                            <?php elseif (in_array($slot['result'], ['missed', 'missed_offline'], true)): ?>
                                <?= e(t('problems.open')) ?>
                            <?php endif ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>

</div>

<div data-part="runs">
<section class="panel<?= $runs === [] ? ' panel-empty' : '' ?>">
    <h2><?= e(t('agent.runs')) ?></h2>
    <?php if ($runs === []): ?>
        <p class="empty"><?= e(t('runs.empty')) ?></p>
    <?php else: ?>
        <?php $showAgent = false; include __DIR__ . '/../runs/_table.php'; ?>
    <?php endif ?>
</section>

</div>

<div data-part="commands">
<?php if ($commands !== []): ?>
    <section class="panel">
        <h2><?= e(t('commands.title')) ?></h2>
        <?php $showTask = false; $back = $taskUrl; include __DIR__ . '/../agents/_commands.php'; ?>
    </section>
<?php endif ?>
</div>
