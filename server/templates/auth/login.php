<?php /** @var string $login @var ?string $error */ ?>
<section class="login">
    <h1><?= e(t('login.title')) ?></h1>
    <?php if ($error !== null): ?>
        <div class="flash flash-error"><?= e($error) ?></div>
    <?php endif ?>
    <form method="post" action="<?= e(url('/login')) ?>" class="stack">
        <?= csrf_field() ?>
        <label><?= e(t('login.login')) ?>
            <input type="text" name="login" value="<?= e($login) ?>" autocomplete="username" required autofocus>
        </label>
        <label><?= e(t('login.password')) ?>
            <input type="password" name="password" autocomplete="current-password" required>
        </label>
        <button type="submit"><?= e(t('login.submit')) ?></button>
    </form>
</section>
