<?php
/** @var list<array> $notices @var int $unread @var int $page @var int $pages */
?>
<?php /* Live parts: new notices and the unread count show up by themselves (app.js). */ ?>
<div class="page-head" data-part="head">
    <h1><?= e(t('notices.title')) ?></h1>
    <?php if ($unread > 0): ?>
        <div class="page-tools">
            <form method="post" action="<?= e(url('/notices/read')) ?>">
                <?= csrf_field() ?>
                <button type="submit" class="secondary"><?= icon('check') ?><?= e(t('notices.read_all')) ?> (<?= $unread ?>)</button>
            </form>
        </div>
    <?php endif ?>
</div>
<p class="hint"><?= e(t('notices.hint')) ?></p>
<div data-part="list">
<?php if ($notices === []): ?>
    <p class="empty"><?= e(t('notices.empty')) ?></p>
<?php else: ?>
    <section class="panel">
        <div class="table-wrap">
            <table class="cards">
                <thead><tr><th><?= e(t('notices.col.time')) ?></th><th><?= e(t('notices.col.text')) ?></th><th></th></tr></thead>
                <tbody>
                <?php foreach ($notices as $n): ?>
                    <?php
                    $link = null;
                    if (in_array($n['kind'], ['site.update', 'site.updated', 'site.update_failed', 'client.release', 'legacy.release', 'macos.release', 'linux.release', 'sevenzip.outdated'], true)) {
                        $link = url('/updates');
                    } elseif ($n['agent_id'] !== null && $n['hostname'] !== null) {
                        $link = $n['task_id'] !== null && !str_starts_with($n['kind'], 'agent.')
                            ? url('/agents/' . $n['agent_id'] . '/tasks/' . $n['task_id']) : url('/agents/' . $n['agent_id']);
                    }
                    ?>
                    <tr class="<?= $n['read_at'] === null ? 'unread' : '' ?>" data-key="n<?= (int) $n['id'] ?>">
                        <td class="nowrap"><?= e(fmt_time($n['created_at'])) ?></td>
                        <td><?php if ($link !== null): ?><a href="<?= e($link) ?>"><?= e(notice_text($n)) ?></a><?php else: ?><?= e(notice_text($n)) ?><?php endif ?>
                            <?php if (!empty(json_decode($n['params'] ?? '', true)['prerelease'])): ?><?= badge('warning', t('releases.channel.preview')) ?><?php endif ?></td>
                        <td class="card-side">
                            <?php if ($n['read_at'] === null): ?>
                                <form method="post" action="<?= e(url('/notices/read')) ?>">
                                    <?= csrf_field() ?><input type="hidden" name="id" value="<?= (int) $n['id'] ?>">
                                    <button type="submit" class="link"><?= e(t('notices.read')) ?></button>
                                </form>
                            <?php else: ?>
                                <span class="note"><?= e(t('notices.was_read')) ?></span>
                            <?php endif ?>
                        </td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    </section>
    <?= pager($page, $pages, '/notices', []) ?>
<?php endif ?>
</div>
