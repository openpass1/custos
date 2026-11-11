<?php
/** @var array $agent from the board @var list<array> $running @var list<string> $queue @var \Custos\Domain\Board $board */
?>
<section class="panel">
    <h2><?= e(t('agent.now')) ?></h2>
    <?php if ($running === []): ?>
        <p><?= e($agent['last_heartbeat_at'] === null ? '—' : ($agent['fresh'] ? t('agent.state.idle') : t('agent.state.unknown'))) ?></p>
    <?php else: ?>
        <?php foreach ($running as $item): ?>
            <?php include __DIR__ . '/_progress.php' ?>
        <?php endforeach ?>
    <?php endif ?>
    <?php if ($queue !== []): ?>
        <p class="note"><?= e(t('agent.queue')) ?>: <?= e(implode(', ', array_map(fn($id) => $board->task($agent['id'], $id)['name'] ?? $id, $queue))) ?></p>
    <?php endif ?>
</section>
