<?php /** @var array $users @var array $me */ ?>
<h1><?= e(t('users.title')) ?></h1>
<p class="hint"><?= e(t('users.hint')) ?></p>

<div class="table-wrap">
    <table>
        <thead>
        <tr>
            <th><?= e(t('users.col.login')) ?></th>
            <th><?= e(t('users.col.status')) ?></th>
            <th><?= e(t('users.col.created')) ?></th>
            <th><?= e(t('users.col.last_login')) ?></th>
            <th><?= e(t('users.col.password')) ?></th>
            <th></th>
        </tr>
        </thead>
        <tbody>
        <?php foreach ($users as $u): ?>
            <tr>
                <td><?= e($u['login']) ?><?php if ((int) $u['id'] === (int) $me['id']): ?> <span class="note"><?= e(t('users.you')) ?></span><?php endif ?></td>
                <td><?= $u['disabled'] ? badge('disabled') : badge('ok', t('users.active')) ?></td>
                <td><?= e(fmt_time($u['created_at'])) ?></td>
                <td><?= e(fmt_time($u['last_login_at'])) ?><?php if ($u['last_login_ip']): ?> <span class="note"><?= e($u['last_login_ip']) ?></span><?php endif ?></td>
                <td>
                    <form method="post" action="<?= e(url('/users/' . $u['id'] . '/password')) ?>" class="inline">
                        <?= csrf_field() ?>
                        <input type="password" name="password" minlength="<?= \Custos\Domain\Users::MIN_PASSWORD ?>" required
                               autocomplete="new-password" aria-label="<?= e(t('users.new_password')) ?>" placeholder="<?= e(t('users.new_password')) ?>">
                        <button type="submit"><?= e(t('users.action.password')) ?></button>
                    </form>
                </td>
                <td>
                    <?php if ($u['disabled']): ?>
                        <form method="post" action="<?= e(url('/users/' . $u['id'] . '/enable')) ?>">
                            <?= csrf_field() ?><button type="submit"><?= e(t('users.action.enable')) ?></button>
                        </form>
                    <?php elseif ((int) $u['id'] !== (int) $me['id']): ?>
                        <form method="post" action="<?= e(url('/users/' . $u['id'] . '/disable')) ?>"
                              data-confirm="<?= e(t('users.confirm.disable', ['login' => $u['login']])) ?>">
                            <?= csrf_field() ?><button type="submit" class="danger"><?= e(t('users.action.disable')) ?></button>
                        </form>
                    <?php endif ?>
                </td>
            </tr>
        <?php endforeach ?>
        </tbody>
    </table>
</div>

<section class="panel narrow">
    <h2><?= e(t('users.create')) ?></h2>
    <form method="post" action="<?= e(url('/users')) ?>" class="stack">
        <?= csrf_field() ?>
        <label><?= e(t('users.col.login')) ?> <input type="text" name="login" maxlength="64" required autocomplete="off"></label>
        <label><?= e(t('login.password')) ?>
            <input type="password" name="password" minlength="<?= \Custos\Domain\Users::MIN_PASSWORD ?>" required autocomplete="new-password">
        </label>
        <button type="submit"><?= e(t('users.action.create')) ?></button>
    </form>
</section>
