<?php
/** @var list<array> $groups */
?>
<p class="crumbs"><a href="<?= e(url('/agents')) ?>"><?= e(t('agents.title')) ?></a></p>
<h1><?= e(t('groups.title')) ?></h1>
<p class="hint"><?= e(t('groups.hint')) ?></p>

<section class="panel">
    <?php if ($groups === []): ?>
        <p class="empty"><?= e(t('groups.empty')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead><tr><th><?= e(t('groups.col.name')) ?></th><th class="num"><?= e(t('groups.col.agents')) ?></th><th></th></tr></thead>
                <tbody>
                <?php foreach ($groups as $g): ?>
                    <tr>
                        <td>
                            <form method="post" action="<?= e(url('/groups/' . $g['id'] . '/rename')) ?>" class="inline">
                                <?= csrf_field() ?>
                                <input type="text" name="name" value="<?= e($g['name']) ?>" maxlength="100" required aria-label="<?= e(t('groups.col.name')) ?>">
                                <button type="submit"><?= e(t('groups.action.rename')) ?></button>
                            </form>
                        </td>
                        <td class="num"><a href="<?= e(url('/agents', ['group' => $g['id']])) ?>"><?= (int) $g['agent_count'] ?></a></td>
                        <td>
                            <form method="post" action="<?= e(url('/groups/' . $g['id'] . '/delete')) ?>"
                                  data-confirm="<?= e(t('groups.confirm.delete', ['name' => $g['name']])) ?>">
                                <?= csrf_field() ?>
                                <button type="submit" class="danger"><?= e(t('groups.action.delete')) ?></button>
                            </form>
                        </td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>

<form method="post" action="<?= e(url('/groups')) ?>" class="panel stack">
    <?= csrf_field() ?>
    <h2><?= e(t('groups.create')) ?></h2>
    <label><?= e(t('groups.col.name')) ?>
        <input type="text" name="name" maxlength="100" required placeholder="<?= e(t('groups.placeholder')) ?>">
    </label>
    <button type="submit"><?= e(t('groups.action.create')) ?></button>
</form>
