<?php
/**
 * @var array $agent from the board @var array $task from the board @var list<array> $problems @var list<array> $slots
 * @var list<array> $runs @var ?array $progress
 */
$def = $task['def'];
?>
<p class="crumbs"><a href="<?= e(url('/agents')) ?>"><?= e(t('agents.title')) ?></a> ›
    <a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e($agent['name']) ?></a></p>
<h1><?= e($task['name']) ?> <?= badge($task['status']) ?>
    <?php foreach (array_diff($task['flags'], [$task['status'], 'ok', 'never', 'disabled']) as $flag): ?><?= badge($flag) ?><?php endforeach ?>
    <?php if (!empty($task['deleted'])): ?><?= badge('deleted', t('tasks.deleted')) ?><?php endif ?>
</h1>
<p class="summary"><?= e(task_summary($def)) ?></p>

<div class="columns">
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
            <dd><?= (int) $task['revision'] ?> · <?= e(fmt_time($task['updated_at'])) ?> · <?= e((string) $task['updated_by']) ?></dd>
        </dl>
        <p><a href="<?= e(url('/runs', ['agent' => $agent['id'], 'task' => $task['id']])) ?>"><?= e(t('task.all_runs')) ?></a></p>
    </section>

    <section class="panel">
        <h2><?= e(t('task.now')) ?></h2>
        <?php if ($progress !== null || $agent['running_task'] === $task['id']): ?>
            <?php $item = ['agent' => $agent, 'task' => $task, 'progress' => $progress]; include __DIR__ . '/../agents/_progress.php'; ?>
        <?php else: ?>
            <p class="empty"><?= e(t('task.not_running')) ?></p>
        <?php endif ?>
    </section>
</div>

<section class="panel">
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

<section class="panel">
    <h2><?= e(t('agent.runs')) ?></h2>
    <?php if ($runs === []): ?>
        <p class="empty"><?= e(t('runs.empty')) ?></p>
    <?php else: ?>
        <?php $showAgent = false; include __DIR__ . '/../runs/_table.php'; ?>
    <?php endif ?>
</section>
