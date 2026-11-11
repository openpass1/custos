<?php
/** @var array $task a task from the board @var array $agent */
$tip = [t('status.' . $task['status'])];
$other = array_diff($task['flags'], [$task['status'], 'ok', 'never']);
foreach ($other as $flag) {
    $tip[] = t('status.' . $flag);
}
$tip[] = t('pill.last', ['time' => $task['last_run_at'] === null ? t('tasks.never') : fmt_time($task['last_run_at'])]);
if ($task['next_at'] !== null) {
    $tip[] = t('pill.next', ['time' => fmt_unix($task['next_at'])]);
}
?>
<a class="pill pill-<?= e(status_class($task['status'])) ?>" href="<?= e(url('/agents/' . $task['agent_id'] . '/tasks/' . $task['id'])) ?>"
   title="<?= e($task['name'] . ' — ' . implode(' · ', $tip)) ?>"><?= e($task['name']) ?></a>
