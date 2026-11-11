<?php
/** @var array $agent from the board @var list<array> $tasks tasks from the board */
?>
<section class="panel">
    <h2><?= e(t('agent.tasks')) ?></h2>
    <?php if ($tasks === []): ?>
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
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>
