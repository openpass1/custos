<?php
/** @var list<array> $problems open problems of this machine */
?>
<?php if ($problems !== []): ?>
    <section class="panel panel-bad">
        <h2><?= e(t('agent.problems')) ?></h2>
        <ul class="problems">
            <?php foreach ($problems as $p): ?>
                <li class="problem problem-<?= e($p['kind']) ?>" data-key="p<?= (int) $p['id'] ?>">
                    <?= badge($p['kind']) ?>
                    <a href="<?= e(url('/agents/' . $p['agent_id'] . '/tasks/' . $p['task_id'])) ?>"><?= e($p['task_name'] ?? t('runs.unknown_task')) ?></a>
                    <div class="problem-text"><?= e(problem_text($p)) ?></div>
                    <div class="note"><?= e(t('problem.since', ['time' => fmt_time($p['opened_at']), 'ago' => fmt_ago($p['opened_at'])])) ?></div>
                </li>
            <?php endforeach ?>
        </ul>
    </section>
<?php endif ?>
