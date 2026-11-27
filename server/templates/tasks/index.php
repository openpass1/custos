<?php
/**
 * @var list<array> $tasks tasks from the board with group and agent, in list order @var array<string, int> $counts by group
 * @var bool $hasAgents
 *
 * All tasks of all machines, opened from the "Tasks" tile of the dashboard. The quick choices are the parts of that
 * tile (plus the disabled tasks, which the tile does not count); they and the search work in the page (app.js), and
 * the choices and the list are live parts.
 */
use Custos\Http\Web\Controller\TasksController;

$dots = ['' => '', 'bad' => 'bad', 'warning' => 'warn', 'running' => 'info', 'other' => 'muted', 'ok' => 'ok', 'disabled' => ''];
?>
<h1><?= e(t('tasks.list.title')) ?></h1>

<?php if ($tasks === []): ?>
    <div data-part="list">
        <p class="empty"><?= e(t($hasAgents ? 'tasks.list.empty' : 'agents.empty')) ?>
            <?php if (!$hasAgents): ?><a href="<?= e(url('/settings')) ?>"><?= e(t('agents.empty.link')) ?></a><?php endif ?></p>
    </div>
<?php else: ?>
    <div class="list-tools" data-list-filter="task-list">
        <div class="chips" role="group" aria-label="<?= e(t('tasks.list.chips')) ?>" data-part="chips">
            <?php foreach (['', ...TasksController::LIST_GROUPS] as $key): ?>
                <?php $n = $counts[$key]; $hint = $key === 'other' ? ' title="' . e(t('tasks.list.other_hint')) . '"' : ''; ?>
                <button type="button" class="chip" data-filter="<?= e($key) ?>" aria-pressed="<?= $key === '' ? 'true' : 'false' ?>"<?= $n === 0 && $key !== '' ? ' disabled' : '' ?><?= $hint ?>>
                    <?php if ($dots[$key] !== ''): ?><i class="dot dot-<?= $dots[$key] ?>"></i><?php endif ?><?= e(t('tasks.list.chip.' . ($key === '' ? 'all' : $key))) ?> <b><?= $n ?></b></button>
            <?php endforeach ?>
        </div>
        <div class="list-tools-end">
            <input type="search" class="list-search" data-list-search placeholder="<?= e(t('tasks.list.search')) ?>" aria-label="<?= e(t('tasks.list.search')) ?>">
            <a href="<?= e(url('/agents')) ?>"><?= e(t('tasks.list.machines')) ?></a>
        </div>
    </div>

    <div data-part="list">
        <section class="panel" id="task-list">
            <div class="table-wrap">
                <table class="row-links cards align-middle">
                    <thead>
                    <tr>
                        <th><?= e(t('tasks.col.name')) ?></th>
                        <th><?= e(t('agents.col.name')) ?></th>
                        <th><?= e(t('tasks.col.status')) ?></th>
                        <th><?= e(t('tasks.col.schedule')) ?></th>
                        <th><?= e(t('tasks.col.last_run')) ?></th>
                        <th><?= e(t('tasks.col.next')) ?></th>
                    </tr>
                    </thead>
                    <tbody data-group>
                    <?php foreach ($tasks as $task): ?>
                        <?php
                        $agent = $task['agent'];
                        $summary = task_summary($task['def']);
                        $running = in_array($task['status'], ['running', 'paused'], true);
                        $search = mb_strtolower(implode(' ', [$task['name'], $agent['name'], $agent['hostname'], (string) $agent['group_name']]));
                        ?>
                        <tr data-flags="<?= e($task['group']) ?>" data-search="<?= e($search) ?>" data-key="t<?= e($task['agent_id'] . '/' . $task['id']) ?>"
                            data-sig="<?= e(implode('|', [$task['status'], implode(',', $task['flags']), (string) $task['last_run_id'], $task['queued'] ? 'q' : ''])) ?>">
                            <td><a href="<?= e(url('/agents/' . $task['agent_id'] . '/tasks/' . $task['id'])) ?>"><strong><?= e($task['name']) ?></strong></a>
                                <div class="note task-desc" title="<?= e($summary) ?>"><?= e($summary) ?></div></td>
                            <td class="nowrap" data-label="<?= e(t('agents.col.name')) ?>"><a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e($agent['name']) ?></a>
                                <?php if ($agent['status'] === 'disabled'): ?><div class="note"><?= e(t('status.disabled')) ?></div>
                                <?php elseif (!$agent['online']): ?><div class="note"><?= e(t('status.offline')) ?></div><?php endif ?>
                            </td>
                            <td class="card-side">
                                <?= badge($task['status']) ?>
                                <?php foreach (array_diff($task['flags'], [$task['status'], 'ok', 'never', 'disabled']) as $flag): ?>
                                    <?= badge($flag) ?>
                                <?php endforeach ?>
                                <?php if ($task['queued']): ?><span class="note"><?= e(t('tasks.queued')) ?></span><?php endif ?>
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
                        </tr>
                    <?php endforeach ?>
                    </tbody>
                </table>
            </div>
            <p class="empty" data-list-empty hidden><?= e(t('tasks.list.search.empty')) ?></p>
        </section>
    </div>
<?php endif ?>
