<?php
/** @var array $agent @var array $task @var ?array $list @var array $listData @var array $destinations
 * @var ?array $start @var ?array $job @var ?array $lastRequest @var bool $active @var bool $missing
 */
use Custos\Domain\Agents;

$taskUrl = '/agents/' . $agent['id'] . '/tasks/' . $task['id'];
$restoreUrl = $taskUrl . '/backup-restore';
?>
<p class="crumbs"><a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e(Agents::name($agent)) ?></a> ›
    <a href="<?= e(url($taskUrl)) ?>"><?= e($task['name']) ?></a></p>
<h1><?= e(t('restore.title')) ?></h1>
<p><?= e(t('restore.intro')) ?></p>

<section class="panel" data-part="restore-sets">
    <h2><?= e(t('restore.sets')) ?></h2>
    <form method="post" action="<?= e(url($restoreUrl . '/list')) ?>" class="inline">
        <?= csrf_field() ?><button type="submit" class="secondary"><?= icon('refresh') ?><?= e(t('restore.find')) ?></button>
    </form>
    <?php if ($list !== null): ?>
        <p><?= badge($list['status']) ?> <?= e(fmt_time($list['created_at'])) ?></p>
        <?php if (isset($listData['error'])): ?>
            <p class="alert alert-bad"><?= e(t('restore.error.' . $listData['error'])) ?></p>
        <?php elseif (isset($listData['sets'])): ?>
            <?php if ($listData['partial']): ?><p class="alert alert-warn"><?= e(t('restore.partial')) ?></p><?php endif ?>
            <?php if ($listData['truncated']): ?><p class="hint"><?= e(t('restore.truncated')) ?></p><?php endif ?>
            <?php if ($listData['sets'] === []): ?>
                <p class="empty"><?= e(t('restore.no_sets')) ?></p>
            <?php elseif (!$active): ?>
                <form method="post" class="stack" action="<?= e(url($restoreUrl . '/start')) ?>"
                      data-confirm="<?= e(t('restore.confirm.start')) ?>">
                    <?= csrf_field() ?><input type="hidden" name="list_id" value="<?= e($list['id']) ?>">
                    <label><?= e(t('restore.select')) ?>
                        <select name="set" required>
                            <option value=""><?= e(t('restore.select')) ?></option>
                            <?php foreach ($listData['sets'] as $key => $set): ?>
                                <?php $store = $destinations[$set['destination_id']] ?? $set['destination_id']; ?>
                                <option value="<?= e($key) ?>"><?= e(implode(' · ', [fmt_time($set['started_at']), $set['name'],
                                    t('restore.destination', ['id' => $store]), t('backup.' . $set['backup_type']),
                                    t('restore.storage.' . $set['storage']), fmt_bytes($set['bytes'])])) ?></option>
                            <?php endforeach ?>
                        </select>
                    </label>
                    <button type="submit"><?= icon('restore') ?><?= e(t('restore.start')) ?></button>
                </form>
            <?php endif ?>
        <?php endif ?>
    <?php endif ?>
</section>

<?php if ($start !== null): ?>
<section class="panel" data-part="restore-job">
    <h2><?= e(t('restore.job')) ?></h2>
    <p><?= e(t('restore.request')) ?> <?= badge($start['status']) ?> · <?= e(fmt_time($start['created_at'])) ?></p>
    <?php $startData = json_decode($start['result_data'] ?? 'null', true) ?? []; ?>
    <?php if (isset($startData['error'])): ?>
        <p class="alert alert-bad"><?= e(t('restore.error.' . $startData['error'])) ?></p>
    <?php endif ?>
    <?php if ($job !== null): ?>
        <p><?= e(t($missing ? 'restore.last_known' : 'restore.accepted')) ?> · <?= badge($job['state'], t('restore.state.' . $job['state'])) ?></p>
        <dl class="props">
            <dt><?= e(t('restore.output')) ?></dt>
            <dd><code><?= e('restores/' . $job['job_id']) ?></code> · <?= e(t('restore.output_hint')) ?></dd>
            <?php if (isset($job['files_done'], $job['files_total'])): ?>
                <dt><?= e(t('restore.files')) ?></dt><dd><?= e(t('restore.counts', ['done' => $job['files_done'], 'total' => $job['files_total']])) ?></dd>
            <?php endif ?>
            <?php if (isset($job['files_restored'], $job['files_skipped'], $job['files_failed'], $job['warnings'])): ?>
                <dt><?= e(t('restore.result')) ?></dt><dd><?= e(t('restore.totals', [
                    'restored' => $job['files_restored'], 'skipped' => $job['files_skipped'],
                    'failed' => $job['files_failed'], 'warnings' => $job['warnings'],
                ])) ?></dd>
            <?php endif ?>
        </dl>
    <?php endif ?>
    <?php if ($lastRequest !== null): ?>
        <p><?= e(t('command.' . $lastRequest['kind'])) ?> <?= badge($lastRequest['status']) ?> · <?= e(fmt_time($lastRequest['result_at'] ?? $lastRequest['created_at'])) ?></p>
        <?php $lastData = json_decode($lastRequest['result_data'] ?? 'null', true) ?? []; ?>
        <?php if (isset($lastData['error'])): ?><p class="alert alert-warn"><?= e(t('restore.error.' . $lastData['error'])) ?></p><?php endif ?>
    <?php endif ?>
    <?php if ($active && ($start['status'] === 'queued' || $job !== null)): ?>
        <div class="buttons">
            <?php if ($job !== null): ?>
                <form method="post" action="<?= e(url($restoreUrl . '/status')) ?>">
                    <?= csrf_field() ?><input type="hidden" name="job_id" value="<?= e($start['id']) ?>">
                    <button type="submit" class="secondary"><?= icon('refresh') ?><?= e(t('restore.status')) ?></button>
                </form>
            <?php endif ?>
            <form method="post" action="<?= e(url($restoreUrl . '/stop')) ?>" data-confirm="<?= e(t('restore.confirm.stop')) ?>">
                <?= csrf_field() ?><input type="hidden" name="job_id" value="<?= e($start['id']) ?>">
                <button type="submit" class="danger"><?= icon('stop') ?><?= e(t('restore.stop')) ?></button>
            </form>
        </div>
    <?php endif ?>
</section>
<?php else: ?>
    <div data-part="restore-job"></div>
<?php endif ?>
