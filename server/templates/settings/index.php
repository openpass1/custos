<?php
/**
 * @var array $values @var array $errors @var ?string $registrationCode @var string $serverFingerprint
 * @var string $serverUrl @var string $myIp
 */
$error = fn(string $name) => isset($errors[$name]) ? '<div class="field-error">' . e($errors[$name]) . '</div>' : '';
?>
<h1><?= e(t('settings.title')) ?></h1>

<section class="panel">
    <h2><?= e(t('settings.agents')) ?></h2>
    <p class="hint"><?= e(t('settings.agents.hint')) ?></p>
    <dl class="props">
        <dt><?= e(t('settings.server_url')) ?></dt>
        <dd><code><?= e($serverUrl !== '' ? $serverUrl : '—') ?></code></dd>
        <dt><?= e(t('settings.registration_code')) ?></dt>
        <dd>
            <?php if ($registrationCode === null): ?>
                <?= e(t('settings.registration_code.none')) ?>
            <?php else: ?>
                <span class="copy-row">
                    <input type="text" id="registration-code" class="mono" value="<?= e($registrationCode) ?>" readonly size="52">
                    <button type="button" data-copy="registration-code"><?= e(t('common.copy')) ?></button>
                </span>
            <?php endif ?>
        </dd>
        <dt><?= e(t('settings.server_fingerprint')) ?></dt>
        <dd><code class="fingerprint"><?= e($serverFingerprint) ?></code></dd>
    </dl>
    <form method="post" action="<?= e(url('/settings/registration-key')) ?>" data-confirm="<?= e(t('settings.confirm.key')) ?>">
        <?= csrf_field() ?>
        <button type="submit"><?= e(t('settings.action.key')) ?></button>
        <span class="note"><?= e(t('settings.key.hint')) ?></span>
    </form>
</section>

<form method="post" action="<?= e(url('/settings')) ?>" class="panel stack">
    <?= csrf_field() ?>
    <h2><?= e(t('settings.general')) ?></h2>
    <label><?= e(t('settings.server_name')) ?>
        <input type="text" name="server_name" value="<?= e($values['server_name']) ?>" maxlength="100" required>
    </label>
    <?= $error('server_name') ?>
    <label><?= e(t('settings.heartbeat_interval_s')) ?>
        <input type="number" name="heartbeat_interval_s" value="<?= e($values['heartbeat_interval_s']) ?>" min="10" max="3600" required>
    </label>
    <?= $error('heartbeat_interval_s') ?>
    <label><?= e(t('settings.offline_after_min')) ?>
        <input type="number" name="offline_after_min" value="<?= e($values['offline_after_min']) ?>" min="1" max="10080" required>
    </label>
    <?= $error('offline_after_min') ?>

    <h2><?= e(t('settings.networks')) ?></h2>
    <p class="hint"><?= e(t('settings.networks.hint', ['ip' => $myIp])) ?></p>
    <label><?= e(t('settings.ip_allow_site')) ?>
        <textarea name="ip_allow_site" rows="3" class="mono" placeholder="192.168.1.0/24"><?= e($values['ip_allow_site']) ?></textarea>
    </label>
    <?= $error('ip_allow_site') ?>
    <label><?= e(t('settings.ip_allow_agents')) ?>
        <textarea name="ip_allow_agents" rows="3" class="mono" placeholder="192.168.1.0/24"><?= e($values['ip_allow_agents']) ?></textarea>
    </label>
    <?= $error('ip_allow_agents') ?>

    <button type="submit"><?= e(t('common.save')) ?></button>
</form>
