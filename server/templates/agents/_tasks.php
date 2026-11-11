<?php
/**
 * @var array $agent from the board @var list<array> $tasks tasks from the board
 * @var array<string, array{status: string, name: string}> $edits edits from the site not confirmed yet, by task id
 */
$approved = $agent['status'] === 'approved';
$created = array_diff_key($edits, array_column($tasks, null, 'id'));
$button = function (array $task, string $action, string $class = 'link') use ($agent): string {
    return '<form method="post" action="' . e(url('/agents/' . $agent['id'] . '/tasks/' . $task['id'] . '/' . $action)) . '"'
        . ($action === 'delete' ? ' data-confirm="' . e(t('task.confirm.delete', ['name' => $task['name']])) . '"' : '')
        . ($action === 'stop' ? ' data-confirm="' . e(t('task.confirm.stop')) . '"' : '') . '>'
        . csrf_field() . '<input type="hidden" name="back" value="agent"><button type="submit" class="' . $class . '">'
        . e(t('task.action.' . $action)) . '</button></form>';
};
?>
<section class="panel">
    <h2><?= e(t('agent.tasks')) ?>
        <?php if ($approved): ?><a class="h2-link" href="<?= e(url('/agents/' . $agent['id'] . '/tasks/new')) ?>"><?= e(t('agent.action.add_task')) ?></a><?php endif ?></h2>
    <?php if ($tasks === [] && $created === []): ?>
        <p class="empty"><?= e(t('agent.tasks.empty')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead>
                <tr>
                    <th><?= e(t('tasks.col.name')) ?></th>
                    <th><?= e(t('tasks.col.status')) ?></th>
                    <th><?= e(t('tasks.col.schedule')) ?></th>
                    <th><?= e(t('tasks.col.last_run')) ?></th>
                    <th><?= e(t('tasks.col.last_success')) ?></th>
                    <th><?= e(t('tasks.col.next')) ?></th>
                    <?php if ($approved): ?><th></th><?php endif ?>
                </tr>
                </thead>
                <tbody>
                <?php foreach ($tasks as $task): ?>
                    <tr>
                        <td><a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/' . $task['id'])) ?>"><strong><?= e($task['name']) ?></strong></a>
                            <div class="note"><?= e(task_summary($task['def'])) ?></div></td>
                        <td>
                            <?= badge($task['status']) ?>
                            <?php foreach (array_diff($task['flags'], [$task['status'], 'ok', 'never', 'disabled']) as $flag): ?>
                                <?= badge($flag) ?>
                            <?php endforeach ?>
                            <?php if ($task['queued']): ?><span class="note"><?= e(t('tasks.queued')) ?></span><?php endif ?>
                            <?php if (isset($edits[$task['id']])): ?><div class="note"><?= e(t('tasks.edit_pending')) ?> <?= badge($edits[$task['id']]['status']) ?></div><?php endif ?>
                        </td>
                        <td><?= e(schedule_label($task['schedule'])) ?></td>
                        <td class="nowrap">
                            <?php if ($task['last_run_id'] !== null): ?>
                                <a href="<?= e(url('/runs/' . $task['last_run_id'])) ?>"><?= badge($task['last_run_status']) ?></a>
                                <?= e(fmt_time($task['last_run_at'])) ?>
                            <?php else: ?>
                                <?= e(t('tasks.never')) ?>
                            <?php endif ?>
                        </td>
                        <td class="nowrap"><?= e(fmt_time($task['last_success_at'])) ?></td>
                        <td class="nowrap"><?= e(fmt_unix($task['next_at'])) ?></td>
                        <?php if ($approved): ?>
                            <td class="task-actions">
                                <?= in_array($task['status'], ['running', 'paused'], true) ? $button($task, 'stop', 'link danger-link') : $button($task, 'run') ?>
                                <a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/' . $task['id'] . '/edit')) ?>"><?= e(t('task.action.edit')) ?></a>
                                <details class="menu">
                                    <summary><?= e(t('task.action.more')) ?></summary>
                                    <div class="menu-items">
                                        <?php if ($task['status'] === 'paused'): ?><?= $button($task, 'resume') ?><?php elseif ($task['status'] === 'running'): ?><?= $button($task, 'pause') ?><?php endif ?>
                                        <?= !empty($task['def']['enabled']) ? $button($task, 'disable') : $button($task, 'enable') ?>
                                        <a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/new', ['from' => $task['id']])) ?>"><?= e(t('task.action.clone')) ?></a>
                                        <a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/' . $task['id'] . '/versions')) ?>"><?= e(t('task.action.history')) ?></a>
                                        <a href="<?= e(url('/templates/new', ['agent' => $agent['id'], 'task' => $task['id']])) ?>"><?= e(t('task.action.template')) ?></a>
                                        <?= $button($task, 'delete', 'link danger-link') ?>
                                    </div>
                                </details>
                            </td>
                        <?php endif ?>
                    </tr>
                <?php endforeach ?>
                <?php foreach ($created as $taskId => $edit): ?>
                    <tr>
                        <td><a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/' . $taskId)) ?>"><strong><?= e($edit['name']) ?></strong></a></td>
                        <td colspan="<?= $approved ? 6 : 5 ?>"><?= e(t('tasks.created_pending')) ?> <?= badge($edit['status']) ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>
