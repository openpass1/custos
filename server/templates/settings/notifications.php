<?php
/**
 * @var array $values @var array $errors @var array<string, bool> $secretSet @var list<string> $channels
 * @var list<array> $latest @var string $siteUrl
 */
$error = fn(string $name) => isset($errors[$name]) ? '<div class="field-error">' . e($errors[$name]) . '</div>' : '';
$secret = function (string $name) use ($secretSet): string {
    $set = $secretSet[$name];
    $html = '<input type="password" name="' . e($name) . '" value="" autocomplete="new-password" placeholder="'
        . e($set ? t('notifications.secret.keep') : '') . '">';
    if ($set) {
        $html .= '<span class="check"><input type="checkbox" name="clear_' . e($name) . '" value="1"> ' . e(t('notifications.secret.clear')) . '</span>';
    }
    return $html;
};
?>
<h1><?= e(t('settings.title')) ?></h1>
<nav class="tabs">
    <a href="<?= e(url('/settings')) ?>"><?= e(t('settings.tab.general')) ?></a>
    <a class="active" href="<?= e(url('/settings/notifications')) ?>"><?= e(t('settings.tab.notifications')) ?></a>
    <a href="<?= e(url('/settings/integrations')) ?>"><?= e(t('integrations.title')) ?></a>
    <a href="<?= e(url('/groups')) ?>"><?= e(t('groups.title')) ?></a>
</nav>
<p class="hint"><?= e(t('notifications.hint')) ?></p>
<?php if ($siteUrl === ''): ?>
    <div class="alert alert-warn"><?= e(t('notifications.no_url')) ?></div>
<?php endif ?>

<form method="post" action="<?= e(url('/settings/notifications')) ?>" class="stack" autocomplete="off">
    <?= csrf_field() ?>
    <section class="panel stack">
        <h2>Telegram <?= in_array('telegram', $channels, true) ? badge('ok', t('notifications.on')) : badge('disabled', t('notifications.off')) ?></h2>
        <p class="hint"><?= e(t('notifications.telegram.hint')) ?></p>
        <label><?= e(t('notifications.telegram_token')) ?>
            <?= $secret('telegram_token') ?>
        </label>
        <?= $error('telegram_token') ?>
        <label><?= e(t('notifications.telegram_chat_ids')) ?>
            <textarea name="telegram_chat_ids" rows="2" class="mono" placeholder="-1001234567890"><?= e($values['telegram_chat_ids']) ?></textarea>
            <span class="note"><?= e(t('notifications.telegram_chat_ids.hint')) ?></span>
        </label>
        <?= $error('telegram_chat_ids') ?>
        <label><?= e(t('notifications.telegram_proxy')) ?>
            <input type="text" name="telegram_proxy" value="<?= e($values['telegram_proxy']) ?>" class="mono" placeholder="socks5h://192.0.2.1:1080">
            <span class="note"><?= e(t('notifications.telegram_proxy.hint')) ?></span>
        </label>
        <?= $error('telegram_proxy') ?>
        <div class="buttons">
            <button type="submit" name="test" value=""><?= e(t('common.save')) ?></button>
            <button type="submit" name="test" value="telegram" class="secondary"><?= e(t('notifications.test.telegram')) ?></button>
        </div>
    </section>

    <section class="panel stack" data-smtp>
        <h2><?= e(t('notifications.email')) ?> <?= in_array('email', $channels, true) ? badge('ok', t('notifications.on')) : badge('disabled', t('notifications.off')) ?></h2>
        <label><?= e(t('notifications.smtp_host')) ?>
            <input type="text" name="smtp_host" value="<?= e($values['smtp_host']) ?>" placeholder="smtp.example.org">
        </label>
        <?= $error('smtp_host') ?>
        <label><?= e(t('notifications.smtp_port')) ?>
            <input type="number" name="smtp_port" value="<?= e($values['smtp_port']) ?>" min="1" max="65535">
            <span class="note"><?= e(t('notifications.smtp_port.hint')) ?></span>
        </label>
        <?= $error('smtp_port') ?>
        <label><?= e(t('notifications.smtp_encryption')) ?>
            <select name="smtp_encryption">
                <?php foreach (\Custos\Infra\Mailer::ENCRYPTIONS as $enc): ?>
                    <option value="<?= e($enc) ?>"<?= $values['smtp_encryption'] === $enc ? ' selected' : '' ?>><?= e(t('notifications.encryption.' . $enc)) ?></option>
                <?php endforeach ?>
            </select>
        </label>
        <label><?= e(t('notifications.smtp_user')) ?>
            <input type="text" name="smtp_user" value="<?= e($values['smtp_user']) ?>" autocomplete="off">
            <span class="note"><?= e(t('notifications.smtp_user.hint')) ?></span>
        </label>
        <label><?= e(t('notifications.smtp_password')) ?>
            <?= $secret('smtp_password') ?>
        </label>
        <label><?= e(t('notifications.mail_from')) ?>
            <input type="email" name="mail_from" value="<?= e($values['mail_from']) ?>" placeholder="custos@example.org">
            <span class="note"><?= e(t('notifications.mail_from.hint')) ?></span>
        </label>
        <?= $error('mail_from') ?>
        <label><?= e(t('notifications.mail_to')) ?>
            <textarea name="mail_to" rows="2" placeholder="admin@example.org"><?= e($values['mail_to']) ?></textarea>
            <span class="note"><?= e(t('notifications.mail_to.hint')) ?></span>
        </label>
        <?= $error('mail_to') ?>
        <div class="buttons">
            <button type="submit" name="test" value=""><?= e(t('common.save')) ?></button>
            <button type="submit" name="test" value="email" class="secondary"><?= e(t('notifications.test.email')) ?></button>
        </div>
    </section>
</form>

<section class="panel">
    <h2><?= e(t('notifications.latest')) ?></h2>
    <?php if ($latest === []): ?>
        <p class="empty"><?= e(t('notifications.latest.none')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead><tr><th><?= e(t('notifications.col.time')) ?></th><th><?= e(t('notifications.col.channel')) ?></th>
                    <th><?= e(t('notifications.col.what')) ?></th><th><?= e(t('notifications.col.status')) ?></th></tr></thead>
                <tbody>
                <?php foreach ($latest as $n): ?>
                    <tr>
                        <td class="nowrap"><?= e(fmt_time($n['created_at'])) ?></td>
                        <td><?= e($n['channel'] === 'telegram' ? 'Telegram' : t('notifications.email')) ?> <span class="note mono"><?= e($n['target']) ?></span></td>
                        <td><?= e($n['is_test'] ? t('notifications.test') : t('notifications.problems', ['n' => $n['problem_count']])) ?></td>
                        <td><?= badge($n['status'], t('notification.' . $n['status'])) ?>
                            <?php if ($n['last_error'] !== null): ?><div class="note bad-text"><?= e($n['last_error']) ?></div><?php endif ?>
                            <?php if ($n['status'] === 'queued' && (int) $n['attempts'] > 0): ?><div class="note"><?= e(t('notifications.retry', ['n' => $n['attempts']])) ?></div><?php endif ?>
                        </td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>
