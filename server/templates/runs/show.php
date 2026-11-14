<?php
/**
 * @var array $run @var array $destinations @var array $destinationInfo @var ?array $log @var ?array $logRequest
 * @var ?array $verify @var ?array $retention @var list<array> $dumps
 *
 * While the run goes on or its log is not here yet, the page refreshes itself (live parts, app.js): the result and a
 * log requested from the agent show up without reloading.
 */
use Custos\Domain\Agents;
?>
<p class="crumbs">
    <a href="<?= e(url('/agents/' . $run['agent_id'])) ?>"><?= e(Agents::name($run)) ?></a> ·
    <a href="<?= e(url('/agents/' . $run['agent_id'] . '/tasks/' . $run['task_id'])) ?>"><?= e($run['task_name'] ?? t('runs.unknown_task')) ?></a>
</p>
<div data-part="run">
<h1><?= e(t('run.title', ['time' => fmt_time($run['started_at'])])) ?> <?= badge($run['status']) ?>
    <?php if ($run['anomaly'] !== null): ?><?= badge('anomaly') ?><?php endif ?></h1>

<?php if ($run['anomaly'] !== null): ?>
    <div class="alert alert-warn"><?= e(t('run.anomaly', ['what' => anomaly_text($run['anomaly'])])) ?></div>
<?php endif ?>

<?php if ($run['error_message'] !== null && $run['error_message'] !== ''): ?>
    <div class="flash flash-error"><?= e($run['error_message']) ?></div>
<?php endif ?>

<div class="columns">
    <section class="panel">
        <h2><?= e(t('run.summary')) ?></h2>
        <dl class="props">
            <dt><?= e(t('runs.col.type')) ?></dt><dd><?= e($run['backup_type'] === null ? '—' : t('backup.' . $run['backup_type'])) ?></dd>
            <dt><?= e(t('runs.col.trigger')) ?></dt><dd><?= e($run['trigger_type'] === null ? '—' : t('trigger.' . $run['trigger_type'])) ?></dd>
            <dt><?= e(t('run.scheduled_for')) ?></dt><dd><?= e(fmt_time($run['scheduled_for'])) ?></dd>
            <dt><?= e(t('run.started')) ?></dt><dd><?= e(fmt_time($run['started_at'], true)) ?></dd>
            <dt><?= e(t('run.finished')) ?></dt><dd><?= e(fmt_time($run['finished_at'], true)) ?></dd>
            <dt><?= e(t('runs.col.duration')) ?></dt><dd><?= e(fmt_duration($run['duration_s'])) ?></dd>
            <dt><?= e(t('run.task_revision')) ?></dt><dd><?= e($run['task_revision'] ?? '—') ?></dd>
        </dl>
    </section>
    <section class="panel">
        <h2><?= e(t('run.volume')) ?></h2>
        <dl class="props">
            <dt><?= e(t('run.source')) ?></dt>
            <dd><?= e(t('run.files_bytes', ['files' => fmt_number($run['source_files']), 'bytes' => fmt_bytes($run['source_bytes'])])) ?></dd>
            <dt><?= e(t('run.processed')) ?></dt><dd><?= e(fmt_number($run['files_processed'])) ?></dd>
            <dt><?= e(t('runs.col.written')) ?></dt><dd><?= e(fmt_bytes($run['bytes_written'])) ?></dd>
            <dt><?= e(t('run.errors')) ?></dt><dd><?= e($run['errors'] ?? '—') ?></dd>
            <dt><?= e(t('run.warnings')) ?></dt><dd><?= e($run['warnings'] ?? '—') ?></dd>
            <dt><?= e(t('run.verify')) ?></dt>
            <dd>
                <?php if (!is_array($verify) || empty($verify['performed'])): ?>
                    <?= e(t('run.verify.none')) ?>
                <?php else: ?>
                    <?= badge(!empty($verify['ok']) ? 'success' : 'error', t(!empty($verify['ok']) ? 'run.verify.ok' : 'run.verify.failed')) ?>
                    <?= e(t('run.verify.scope.' . ($verify['scope'] ?? 'new'))) ?>, <?= e(t('run.verify.sets', ['n' => (int) ($verify['sets_checked'] ?? 0)])) ?>
                    <?php foreach ((array) ($verify['failed_sets'] ?? []) as $set): ?>
                        <div class="bad-text"><?= e(t('run.verify.failed_set', ['name' => is_scalar($set) ? $set : json_encode($set)])) ?></div>
                    <?php endforeach ?>
                <?php endif ?>
            </dd>
            <dt><?= e(t('run.retention')) ?></dt>
            <dd><?= e(is_array($retention) ? t('run.retention.deleted', ['n' => (int) ($retention['deleted_sets'] ?? 0)]) : '—') ?></dd>
        </dl>
    </section>
</div>

<?php if ($destinations !== []): ?>
    <section class="panel">
        <h2><?= e(t('run.destinations')) ?></h2>
        <div class="table-wrap">
            <table>
                <thead><tr>
                    <th><?= e(t('run.destination')) ?></th><th><?= e(t('runs.col.status')) ?></th>
                    <th><?= e(t('run.set')) ?></th><th class="num"><?= e(t('run.bytes')) ?></th><th><?= e(t('run.error')) ?></th>
                </tr></thead>
                <tbody>
                <?php foreach ($destinations as $d): ?>
                    <tr>
                        <td class="mono"><?= e($destinationInfo[$d['destination_id']] ?? $d['destination_id']) ?></td>
                        <td><?= badge($d['status']) ?></td>
                        <td><?= e($d['set_name'] ?? '—') ?></td>
                        <td class="num"><?= e(fmt_bytes($d['bytes'])) ?></td>
                        <td><?= e($d['error'] ?? '') ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    </section>
<?php endif ?>

<?php if ($dumps !== []): ?>
    <section class="panel">
        <h2><?= e(t('run.databases')) ?></h2>
        <div class="table-wrap">
            <table>
                <thead><tr>
                    <th><?= e(t('run.database')) ?></th><th><?= e(t('run.database.engine')) ?></th><th><?= e(t('runs.col.status')) ?></th>
                    <th class="num"><?= e(t('run.bytes')) ?></th><th><?= e(t('run.error')) ?></th>
                </tr></thead>
                <tbody>
                <?php foreach ($dumps as $d): ?>
                    <?php $engine = (string) ($d['engine'] ?? ''); $ok = ($d['ok'] ?? null) !== false && ($d['error'] ?? null) === null; ?>
                    <tr<?= $ok ? '' : ' class="bad-row"' ?>>
                        <td class="mono"><?= e((string) ($d['name'] ?? '')) ?></td>
                        <td><?= e(t('db.engine.' . $engine) === 'db.engine.' . $engine ? $engine : t('db.engine.' . $engine)) ?></td>
                        <td><?= badge($ok ? 'success' : 'error') ?></td>
                        <td class="num"><?= e(fmt_bytes(isset($d['bytes']) && is_int($d['bytes']) ? $d['bytes'] : null)) ?></td>
                        <td><?= e((string) ($d['error'] ?? '')) ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    </section>
<?php endif ?>

<section class="panel">
    <h2><?= e(t('run.log')) ?></h2>
    <?php if ($log !== null): ?>
        <p>
            <a href="<?= e(url('/runs/' . $run['id'] . '/log')) ?>"><?= e(t('run.log.open')) ?></a> ·
            <a href="<?= e(url('/runs/' . $run['id'] . '/log/download')) ?>"><?= e(t('run.log.download', ['size' => fmt_bytes($log['size'])])) ?></a>
        </p>
    <?php else: ?>
        <p class="note"><?= e(t('run.log.none')) ?></p>
        <?php if ($run['finished_at'] !== null): ?>
            <form method="post" action="<?= e(url('/runs/' . $run['id'] . '/fetch-log')) ?>" class="inline">
                <?= csrf_field() ?>
                <button type="submit" class="secondary"><?= e(t('run.log.request')) ?></button>
                <?php if ($logRequest !== null): ?>
                    <span class="note"><?= e(t('run.log.request_state', ['time' => fmt_time($logRequest['created_at'])])) ?>
                        <?= badge($logRequest['status']) ?><?= $logRequest['result_message'] ? ' ' . e($logRequest['result_message']) : '' ?></span>
                <?php endif ?>
            </form>
        <?php endif ?>
    <?php endif ?>
    <?php if ($run['log_tail'] !== null && $run['log_tail'] !== ''): ?>
        <h3><?= e(t('run.log_tail')) ?></h3>
        <pre class="log"><?= e($run['log_tail']) ?></pre>
    <?php endif ?>
</section>
</div>
