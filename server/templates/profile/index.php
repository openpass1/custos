<?php
/** @var array $me @var array<string, string> $languages @var string $defaultLanguage */
use Custos\Domain\Users;

$current = (string) ($me['language'] ?? '');
?>
<h1><?= e(t('profile.title')) ?> · <?= e($me['login']) ?></h1>

<div class="columns">
    <form method="post" action="<?= e(url('/profile')) ?>" class="panel stack">
        <?= csrf_field() ?>
        <h2><?= e(t('profile.look')) ?></h2>
        <label><?= e(t('profile.language')) ?>
            <select name="language">
                <option value=""<?= selected('', $current) ?>><?= e(t('profile.language.default', ['name' => $languages[$defaultLanguage] ?? $defaultLanguage])) ?></option>
                <?php foreach ($languages as $code => $name): ?>
                    <option value="<?= e($code) ?>"<?= selected($code, $current) ?>><?= e($name) ?></option>
                <?php endforeach ?>
            </select>
        </label>
        <fieldset class="plain">
            <legend><?= e(t('profile.theme')) ?></legend>
            <div class="choices">
                <?php foreach (Users::THEMES as $theme): ?>
                    <label class="choice"><input type="radio" name="theme" value="<?= e($theme) ?>"<?= checked(($me['theme'] ?? 'auto') === $theme) ?>>
                        <span><?= e(t('profile.theme.' . $theme)) ?></span></label>
                <?php endforeach ?>
            </div>
        </fieldset>
        <button type="submit"><?= e(t('common.save')) ?></button>
    </form>

    <form method="post" action="<?= e(url('/profile/password')) ?>" class="panel stack">
        <?= csrf_field() ?>
        <h2><?= e(t('profile.password')) ?></h2>
        <label><?= e(t('profile.password.current')) ?>
            <input type="password" name="current" required autocomplete="current-password">
        </label>
        <label><?= e(t('profile.password.new', ['min' => Users::MIN_PASSWORD])) ?>
            <input type="password" name="password" minlength="<?= Users::MIN_PASSWORD ?>" required autocomplete="new-password">
        </label>
        <label><?= e(t('profile.password.repeat')) ?>
            <input type="password" name="repeat" minlength="<?= Users::MIN_PASSWORD ?>" required autocomplete="new-password">
        </label>
        <button type="submit"><?= e(t('profile.password.save')) ?></button>
    </form>
</div>
