<?php
/**
 * @var array $agent from the board @var list<array> $running @var list<string> $queue @var \Custos\Domain\Board $board
 * @var list<array> $tasks tasks from the board
 *
 * While nothing runs this is one line with the nearest scheduled run; a run in progress gets its panel.
 */
$queued = $queue === [] ? '' : t('agent.queue') . ': ' . implode(', ', array_map(fn($id) => $board->task($agent['id'], $id)['name'] ?? $id, $queue));
?>
<?php if ($running === []): ?>
    <?php
    $next = null;
    foreach ($tasks as $task) {
        if ($task['next_at'] !== null && ($next === null || $task['next_at'] < $next['next_at'])) {
            $next = $task;
        }
    }
    ?>
    <p class="now-line"><?= icon('clock') ?>
        <span><?= e($agent['last_heartbeat_at'] === null ? '—' : ($agent['fresh'] ? t('agent.state.idle') : t('agent.state.unknown'))) ?>
            <?php if ($next !== null && $agent['fresh']): ?><?= e(t('agent.now.next', ['task' => $next['name'], 'time' => fmt_unix($next['next_at'])])) ?><?php endif ?></span>
        <?php if ($queued !== ''): ?><span><?= e($queued) ?></span><?php endif ?>
    </p>
<?php else: ?>
    <section class="panel">
        <h2><?= e(t('agent.now')) ?></h2>
        <?php foreach ($running as $item): ?>
            <?php include __DIR__ . '/_progress.php' ?>
        <?php endforeach ?>
        <?php if ($queued !== ''): ?>
            <p class="note"><?= e($queued) ?></p>
        <?php endif ?>
    </section>
<?php endif ?>
