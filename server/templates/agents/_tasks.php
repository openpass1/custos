<?php
/**
 * @var array $agent from the board @var list<array> $tasks tasks from the board
 * @var array<string, array{status: string, name: string}> $edits edits from the site not confirmed yet, by task id
 *
 * One row per task: the name with its description on one line, the state, the schedule, the last and the next run.
 * A row has one button (run, or stop while it runs) and the rest under "⋯".
 */
$approved = $agent['status'] === 'approved';
$created = array_diff_key($edits, array_column($tasks, null, 'id'));
$taskBase = fn(array $task): string => '/agents/' . $agent['id'] . '/tasks/' . $task['id'];
$button = function (array $task, string $action, string $class, string $iconName) use ($taskBase): string {
    $confirm = match ($action) {
        'delete' => t('task.confirm.delete', ['name' => $task['name']]),
        'stop' => t('task.confirm.stop'),
        default => null,
    };
    return '<form method="post" action="' . e(url($taskBase($task) . '/' . $action)) . '"'
        . ($confirm === null ? '' : ' data-confirm="' . e($confirm) . '"') . '>'
        . csrf_field() . '<input type="hidden" name="back" value="agent"><button type="submit" class="' . e($class) . '">'
        . icon($iconName) . e(t('task.action.' . $action)) . '</button></form>';
};
?>
<section class="panel">
    <h2><?= e(t('agent.tasks')) ?><?php if ($tasks !== []): ?> <span class="count"><?= count($tasks) ?></span><?php endif ?></h2>
    <?php if ($tasks === [] && $created === []): ?>
        <p class="empty"><?= e(t('agent.tasks.empty')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table class="row-links cards">
                <thead>
                <tr>
                    <th><?= e(t('tasks.col.name')) ?></th>
                    <th><?= e(t('tasks.col.status')) ?></th>
                    <th><?= e(t('tasks.col.schedule')) ?></th>
                    <th><?= e(t('tasks.col.last_run')) ?></th>
                    <th><?= e(t('tasks.col.next')) ?></th>
                    <?php if ($approved): ?><th><span class="visually-hidden"><?= e(t('agent.actions')) ?></span></th><?php endif ?>
                </tr>
                </thead>
                <tbody>
                <?php foreach ($tasks as $task): ?>
                    <?php $summary = task_summary($task['def']); $running = in_array($task['status'], ['running', 'paused'], true); ?>
                    <tr data-key="t<?= e($task['id']) ?>" data-sig="<?= e(implode('|', [$task['status'], implode(',', $task['flags']), (string) $task['last_run_id'], $task['queued'] ? 'q' : '', $edits[$task['id']]['status'] ?? ''])) ?>">
                        <td><a href="<?= e(url($taskBase($task))) ?>"><strong><?= e($task['name']) ?></strong></a>
                            <div class="note task-desc" title="<?= e($summary) ?>"><?= e($summary) ?></div></td>
                        <td>
                            <?= badge($task['status']) ?>
                            <?php foreach (array_diff($task['flags'], [$task['status'], 'ok', 'never', 'disabled']) as $flag): ?>
                                <?= badge($flag) ?>
                            <?php endforeach ?>
                            <?php if ($task['queued']): ?><span class="note"><?= e(t('tasks.queued')) ?></span><?php endif ?>
                            <?php if (isset($edits[$task['id']])): ?><div class="note"><?= e(t('tasks.edit_pending')) ?> <?= badge($edits[$task['id']]['status']) ?></div><?php endif ?>
                        </td>
                        <td data-label="<?= e(t('tasks.col.schedule')) ?>"><?= e(schedule_label($task['schedule'])) ?></td>
                        <td class="nowrap" data-label="<?= e(t('tasks.col.last_run')) ?>">
                            <?php if ($task['last_run_id'] !== null): ?>
                                <a href="<?= e(url('/runs/' . $task['last_run_id'])) ?>"><?= e(fmt_time($task['last_run_at'])) ?></a>
                                <?php if ($task['last_run_status'] !== $task['status'] && !$running): ?><?= badge($task['last_run_status']) ?><?php endif ?>
                                <?php if ($task['last_run_status'] !== 'success' && $task['last_success_at'] !== null): ?>
                                    <div class="note"><?= e(t('tasks.last_success_note', ['time' => fmt_time($task['last_success_at'])])) ?></div>
                                <?php endif ?>
                            <?php else: ?>
                                <span class="note"><?= e(t('tasks.never')) ?></span>
                            <?php endif ?>
                        </td>
                        <td class="nowrap" data-label="<?= e(t('tasks.col.next')) ?>"><?= e(fmt_unix($task['next_at'])) ?></td>
                        <?php if ($approved): ?>
                            <td class="row-actions card-side">
                                <?= $running ? $button($task, 'stop', 'small danger', 'stop') : $button($task, 'run', 'small secondary', 'play') ?>
                                <details class="dropdown">
                                    <summary class="dots" title="<?= e(t('common.more')) ?>"><?= icon('dots') ?><span class="visually-hidden"><?= e(t('tasks.more_for', ['name' => $task['name']])) ?></span></summary>
                                    <div class="dropdown-menu">
                                        <a class="menu-item" href="<?= e(url($taskBase($task) . '/edit')) ?>"><?= icon('pencil') ?><?= e(t('task.action.edit')) ?></a>
                                        <?php if ($task['status'] === 'paused'): ?><?= $button($task, 'resume', 'menu-item', 'play') ?>
                                        <?php elseif ($task['status'] === 'running'): ?><?= $button($task, 'pause', 'menu-item', 'pause') ?><?php endif ?>
                                        <?= !empty($task['def']['enabled']) ? $button($task, 'disable', 'menu-item', 'power') : $button($task, 'enable', 'menu-item', 'power') ?>
                                        <a class="menu-item" href="<?= e(url('/agents/' . $agent['id'] . '/tasks/new', ['from' => $task['id']])) ?>"><?= icon('copy') ?><?= e(t('task.action.clone')) ?></a>
                                        <a class="menu-item" href="<?= e(url($taskBase($task) . '/versions')) ?>"><?= icon('clock') ?><?= e(t('task.action.history')) ?></a>
                                        <a class="menu-item" href="<?= e(url('/templates/new', ['agent' => $agent['id'], 'task' => $task['id']])) ?>"><?= icon('template') ?><?= e(t('task.action.template')) ?></a>
                                        <div class="menu-sep"></div>
                                        <?= $button($task, 'delete', 'menu-item danger', 'trash') ?>
                                    </div>
                                </details>
                            </td>
                        <?php endif ?>
                    </tr>
                <?php endforeach ?>
                <?php foreach ($created as $taskId => $edit): ?>
                    <tr>
                        <td><a href="<?= e(url('/agents/' . $agent['id'] . '/tasks/' . $taskId)) ?>"><strong><?= e($edit['name']) ?></strong></a></td>
                        <td colspan="<?= $approved ? 5 : 4 ?>"><?= e(t('tasks.created_pending')) ?> <?= badge($edit['status']) ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>
