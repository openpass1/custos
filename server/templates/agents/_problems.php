<?php
/** @var list<array> $problems open problems of this machine */
?>
<?php if ($problems !== []): ?>
    <section class="panel panel-bad">
        <h2><?= e(t('agent.problems')) ?></h2>
        <ul class="problems">
            <?php foreach ($problems as $p): ?>
                <li class="problem problem-<?= e($p['kind']) ?>" data-key="p<?= (int) $p['id'] ?>">
                    <span class="lead lead-<?= $p['kind'] === 'space' ? 'warn' : 'bad' ?>"><?= icon(['missed' => 'event-busy', 'space' => 'hdd'][$p['kind']] ?? 'error-fill') ?></span>
                    <div class="problem-body">
                        <div class="problem-head"><a href="<?= e(url('/agents/' . $p['agent_id'] . '/tasks/' . $p['task_id'])) ?>"><strong><?= e($p['task_name'] ?? t('runs.unknown_task')) ?></strong></a>
                            <?= badge($p['kind']) ?></div>
                        <div class="problem-text"><?= e(problem_text($p)) ?></div>
                    </div>
                    <div class="problem-since"><span><?= e(t('problem.since_time', ['time' => fmt_time($p['opened_at'])])) ?></span><span><?= e(fmt_ago($p['opened_at'])) ?></span></div>
                </li>
            <?php endforeach ?>
        </ul>
    </section>
<?php endif ?>
