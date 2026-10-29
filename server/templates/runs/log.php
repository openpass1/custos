<?php /** @var array $run @var string $text @var bool $truncated */ ?>
<p class="crumbs"><a href="<?= e(url('/runs/' . $run['id'])) ?>"><?= e(t('run.back')) ?></a></p>
<h1><?= e(t('runs.log.title')) ?> · <?= e($run['task_name'] ?? t('runs.unknown_task')) ?> · <?= e(fmt_time($run['started_at'])) ?></h1>
<p><a href="<?= e(url('/runs/' . $run['id'] . '/log/download')) ?>"><?= e(t('run.log.download_plain')) ?></a></p>
<?php if ($truncated): ?>
    <div class="flash flash-info"><?= e(t('run.log.truncated')) ?></div>
<?php endif ?>
<pre class="log full"><?= e($text) ?></pre>
