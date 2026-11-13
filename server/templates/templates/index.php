<?php
/** @var list<array> $templates */
?>
<div class="page-head">
    <h1><?= e(t('templates.title')) ?></h1>
    <div class="page-tools">
        <a class="button-link primary" href="<?= e(url('/templates/new')) ?>"><?= icon('plus') ?><?= e(t('templates.action.new')) ?></a>
    </div>
</div>
<p class="hint"><?= e(t('templates.hint')) ?></p>
<section class="panel<?= $templates === [] ? ' panel-empty' : '' ?>">
    <?php if ($templates === []): ?>
        <p class="empty"><?= e(t('templates.empty')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead><tr><th><?= e(t('templates.col.name')) ?></th><th><?= e(t('templates.col.revision')) ?></th>
                    <th><?= e(t('templates.col.machines')) ?></th><th><?= e(t('templates.col.updated')) ?></th></tr></thead>
                <tbody>
                <?php foreach ($templates as $tpl): ?>
                    <tr>
                        <td><a href="<?= e(url('/templates/' . $tpl['id'])) ?>"><strong><?= e($tpl['name']) ?></strong></a>
                            <div class="note"><?= e(task_summary(\Custos\Domain\TaskEdits::decode($tpl['data']))) ?></div></td>
                        <td><?= (int) $tpl['revision'] ?></td>
                        <td><?= (int) $tpl['machines'] ?>
                            <?php if ((int) $tpl['outdated'] > 0): ?><?= badge('warning', t('templates.outdated', ['n' => (int) $tpl['outdated']])) ?><?php endif ?></td>
                        <td class="nowrap"><?= e(fmt_time($tpl['updated_at'])) ?> <span class="note"><?= e($tpl['updated_by']) ?></span></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>
