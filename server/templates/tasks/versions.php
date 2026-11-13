<?php
/**
 * @var array $agent @var array $task @var list<array> $versions @var ?array $version @var ?list<array> $diff @var ?string $refusal
 */
use Custos\Domain\Agents;

$latest = (int) ($versions[0]['revision'] ?? 0);
$taskUrl = '/agents/' . $agent['id'] . '/tasks/' . $task['id'];
?>
<p class="crumbs"><a href="<?= e(url('/agents')) ?>"><?= e(t('agents.title')) ?></a> ›
    <a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e(Agents::name($agent)) ?></a> ›
    <a href="<?= e(url($taskUrl)) ?>"><?= e($task['name']) ?></a></p>
<h1><?= e(t('versions.title', ['name' => $task['name']])) ?></h1>
<p class="hint"><?= e(t('versions.hint')) ?></p>

<?php if ($versions === []): ?>
    <p class="empty"><?= e(t('versions.empty')) ?></p>
<?php else: ?>
<div class="versions-grid">
    <section class="panel">
        <h2><?= e(t('versions.list')) ?></h2>
        <div class="table-wrap">
            <table>
                <thead><tr><th><?= e(t('tasks.col.revision')) ?></th><th><?= e(t('versions.col.when')) ?></th><th><?= e(t('versions.col.who')) ?></th></tr></thead>
                <tbody>
                <?php foreach ($versions as $v): ?>
                    <tr<?= $version !== null && (int) $v['revision'] === (int) $version['revision'] ? ' class="selected-row"' : '' ?>>
                        <td><a href="<?= e(url($taskUrl . '/versions', ['rev' => $v['revision']])) ?>"><?= (int) $v['revision'] ?></a>
                            <?php if ((int) $v['revision'] === $latest): ?><span class="note"><?= e(t('versions.current')) ?></span><?php endif ?>
                            <?php if ($v['overwritten']): ?><?= badge('warning', t('versions.overwritten')) ?><?php endif ?></td>
                        <td class="nowrap"><?= e(fmt_time($v['updated_at'] ?? $v['received_at'])) ?></td>
                        <td><?= e((string) $v['updated_by']) ?> <span class="note"><?= e(t('versions.source.' . $v['source'])) ?></span></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    </section>

    <?php if ($version !== null): ?>
    <section class="panel">
        <h2><?= e(t('versions.version', ['n' => (int) $version['revision']])) ?></h2>
        <p class="summary"><?= e(task_summary($version['task'])) ?></p>
        <?php if ($version['overwritten']): ?>
            <p class="note"><?= e(t('versions.overwritten.hint')) ?></p>
        <?php endif ?>
        <?php if ($version['previous'] === null): ?>
            <p class="empty"><?= e(t('versions.first')) ?></p>
        <?php elseif ($diff === []): ?>
            <p class="empty"><?= e(t('versions.same', ['n' => $version['previous_revision']])) ?></p>
        <?php else: ?>
            <h3><?= e(t('versions.diff', ['n' => $version['previous_revision']])) ?></h3>
            <div class="table-wrap">
                <table class="diff">
                    <thead><tr><th><?= e(t('versions.col.setting')) ?></th><th><?= e(t('versions.col.old')) ?></th><th><?= e(t('versions.col.new')) ?></th></tr></thead>
                    <tbody>
                    <?php foreach ($diff as $d): ?>
                        <tr>
                            <td class="mono"><?= e($d['path']) ?></td>
                            <td class="mono old"><?= $d['old'] === null ? '<span class="note">' . e(t('versions.added')) . '</span>' : e($d['old']) ?></td>
                            <td class="mono new"><?= $d['new'] === null ? '<span class="note">' . e(t('versions.removed')) . '</span>' : e($d['new']) ?></td>
                        </tr>
                    <?php endforeach ?>
                    </tbody>
                </table>
            </div>
        <?php endif ?>
        <?php if ((int) $version['revision'] !== $latest && empty($task['deleted'])): ?>
            <?php if ($refusal === null): ?>
                <form method="post" action="<?= e(url($taskUrl . '/versions/' . (int) $version['revision'] . '/restore')) ?>"
                      data-confirm="<?= e(t('versions.confirm.restore', ['n' => (int) $version['revision']])) ?>" class="inline gap-top">
                    <?= csrf_field() ?>
                    <button type="submit" class="secondary"><?= icon('refresh') ?><?= e(t('versions.restore')) ?></button>
                    <span class="note"><?= e(t('versions.restore.hint')) ?></span>
                </form>
            <?php else: ?>
                <p class="note"><?= e($refusal) ?></p>
            <?php endif ?>
        <?php endif ?>
        <details class="gap-top"><summary><?= e(t('versions.json')) ?></summary>
            <pre class="log full"><?= e(json_encode(\Custos\Domain\TaskEdits::object($version['task']), JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)) ?></pre>
        </details>
    </section>
    <?php endif ?>
</div>
<?php endif ?>
