<?php
/** @var \Custos\Domain\Board $board @var array $soft @var bool $cronLate @var ?string $cronLast */
$hasSoft = $soft['offline'] !== [] || $soft['stale'] !== [] || $soft['anomaly'] !== [] || $soft['paused'] !== [];
?>
<section class="panel">
    <h2><?= e(t('dashboard.attention')) ?></h2>
    <?php if ($cronLate): ?>
        <div class="alert alert-bad">
            <?= e($cronLast === null ? t('dashboard.cron.never') : t('dashboard.cron.late', ['time' => fmt_time($cronLast)])) ?>
        </div>
    <?php endif ?>
    <?php if ($board->problems === []): ?>
        <p class="all-good"><?= e(t('dashboard.no_problems')) ?></p>
    <?php else: ?>
        <ul class="problems">
            <?php foreach ($board->problems as $p): ?>
                <?php // A list item: the kind as an icon, the machine and task, what happened, since when. ?>
                <li class="problem problem-<?= e($p['kind']) ?>" data-key="p<?= (int) $p['id'] ?>">
                    <span class="lead lead-<?= $p['kind'] === 'space' ? 'warn' : 'bad' ?>"><?= icon(['missed' => 'event-busy', 'space' => 'hdd'][$p['kind']] ?? 'error-fill') ?></span>
                    <div class="problem-body">
                        <div class="problem-head"><a href="<?= e(url('/agents/' . $p['agent_id'] . '/tasks/' . $p['task_id'])) ?>">
                            <strong><?= e($p['agent']['name']) ?></strong> · <?= e($p['task_name'] ?? t('runs.unknown_task')) ?></a>
                            <?= badge($p['kind']) ?></div>
                        <div class="problem-text"><?= e(problem_text($p)) ?></div>
                    </div>
                    <div class="problem-since"><span><?= e(t('problem.since_time', ['time' => fmt_time($p['opened_at'])])) ?></span><span><?= e(fmt_ago($p['opened_at'])) ?></span></div>
                </li>
            <?php endforeach ?>
        </ul>
    <?php endif ?>
</section>

<?php if ($hasSoft): ?>
    <section class="panel calm">
        <h2><?= e(t('dashboard.soft')) ?></h2>
        <?php if ($soft['offline'] !== []): ?>
            <h3><?= e(t('dashboard.soft.offline')) ?></h3>
            <ul class="soft">
                <?php foreach ($soft['offline'] as $item): ?>
                    <li><a href="<?= e(url('/agents/' . $item['agent']['id'])) ?>"><?= e($item['agent']['name']) ?></a>:
                        <?php foreach ($item['tasks'] as $i => $task): ?><?= $i > 0 ? ', ' : '' ?><a href="<?= e(url('/agents/' . $task['agent_id'] . '/tasks/' . $task['id'])) ?>"><?= e($task['name']) ?></a>
                            <span class="note">(<?= e(fmt_local(end($task['offline_slots']), $item['agent']['time_zone'])) ?><?= count($task['offline_slots']) > 1 ? ', ' . e(t('dashboard.soft.more', ['n' => count($task['offline_slots'])])) : '' ?>)</span><?php endforeach ?>
                        <span class="note">· <?= e(t('dashboard.soft.last_seen', ['ago' => fmt_ago($item['agent']['last_heartbeat_at'])])) ?></span>
                    </li>
                <?php endforeach ?>
            </ul>
        <?php endif ?>
        <?php if ($soft['stale'] !== []): ?>
            <h3><?= e(t('dashboard.soft.stale')) ?></h3>
            <ul class="soft">
                <?php foreach ($soft['stale'] as $task): ?>
                    <li><a href="<?= e(url('/agents/' . $task['agent_id'] . '/tasks/' . $task['id'])) ?>"><?= e($task['agent']['name']) ?> · <?= e($task['name']) ?></a>
                        <span class="note"><?= e($task['last_success_at'] === null ? t('dashboard.soft.never_success') : t('dashboard.soft.last_success', ['time' => fmt_time($task['last_success_at'])])) ?></span></li>
                <?php endforeach ?>
            </ul>
        <?php endif ?>
        <?php if ($soft['anomaly'] !== []): ?>
            <h3><?= e(t('dashboard.soft.anomaly')) ?></h3>
            <ul class="soft">
                <?php foreach ($soft['anomaly'] as $task): ?>
                    <li><a href="<?= e(url('/agents/' . $task['agent_id'] . '/tasks/' . $task['id'])) ?>"><?= e($task['agent']['name']) ?> · <?= e($task['name']) ?></a></li>
                <?php endforeach ?>
            </ul>
        <?php endif ?>
        <?php if ($soft['paused'] !== []): ?>
            <h3><?= e(t('dashboard.soft.paused')) ?></h3>
            <ul class="soft">
                <?php foreach ($soft['paused'] as $task): ?>
                    <li><a href="<?= e(url('/agents/' . $task['agent_id'] . '/tasks/' . $task['id'])) ?>"><?= e($task['agent']['name']) ?> · <?= e($task['name']) ?></a>
                        <?php if ($task['agent']['paused_at'] !== null): ?><span class="note"><?= e(t('agent.paused_since', ['time' => fmt_time($task['agent']['paused_at'])])) ?></span><?php endif ?></li>
                <?php endforeach ?>
            </ul>
        <?php endif ?>
    </section>
<?php endif ?>
