<?php
/** @var list<array> $templates */
?>
<h1><?= e(t('templates.title')) ?></h1>
<p class="hint"><?= e(t('templates.hint')) ?></p>
<div class="actions-row">
    <a class="button-link" href="<?= e(url('/templates/new')) ?>"><?= e(t('templates.action.new')) ?></a>
</div>
<section class="panel">
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
