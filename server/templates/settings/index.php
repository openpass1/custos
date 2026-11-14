<?php
/**
 * @var array $values @var array $errors @var string $serverFingerprint @var string $myIp
 * @var ?string $connection the site address and the registration code in one line (A.3), null if it cannot be built
 * @var string $connectionProblem the message key that says why
 */
$error = fn(string $name) => isset($errors[$name]) ? '<div class="field-error">' . e($errors[$name]) . '</div>' : '';
?>
<h1><?= e(t('settings.title')) ?></h1>
<nav class="tabs">
    <a class="active" href="<?= e(url('/settings')) ?>"><?= e(t('settings.tab.general')) ?></a>
    <a href="<?= e(url('/settings/notifications')) ?>"><?= e(t('settings.tab.notifications')) ?></a>
    <a href="<?= e(url('/settings/integrations')) ?>"><?= e(t('integrations.title')) ?></a>
    <a href="<?= e(url('/groups')) ?>"><?= e(t('groups.title')) ?></a>
</nav>

<section class="panel">
    <h2><?= e(t('settings.agents')) ?></h2>
    <p class="hint"><?= e(t('settings.agents.hint')) ?></p>
    <dl class="props">
        <dt><?= e(t('settings.connection')) ?></dt>
        <dd>
            <?php if ($connection === null): ?>
                <?= e(t($connectionProblem)) ?>
            <?php else: ?>
                <span class="copy-row">
                    <input type="text" id="connection-string" class="mono" value="<?= e($connection) ?>" readonly size="72">
                    <button type="button" class="secondary" data-copy="connection-string" data-copied="<?= e(t('common.copied')) ?>"><?= icon('copy') ?><?= e(t('common.copy')) ?></button>
                </span>
            <?php endif ?>
        </dd>
        <dt><?= e(t('settings.server_fingerprint')) ?></dt>
        <dd><code class="fingerprint"><?= e($serverFingerprint) ?></code></dd>
    </dl>
    <form method="post" action="<?= e(url('/settings/registration-key')) ?>" data-confirm="<?= e(t('settings.confirm.key')) ?>">
        <?= csrf_field() ?>
        <button type="submit" class="secondary"><?= icon('key') ?><?= e(t('settings.action.key')) ?></button>
        <span class="note"><?= e(t('settings.key.hint')) ?></span>
    </form>
</section>

<?php
// Each group is its own small form with its own button; what a form does not send keeps its stored value.
$open = fn(string $title): string => '<form method="post" action="' . e(url('/settings')) . '" class="panel settings-group">'
    . csrf_field() . '<h2>' . e(t($title)) . '</h2><div class="field-grid">';
$close = '</div><div class="form-buttons"><button type="submit">' . e(t('common.save')) . '</button></div></form>';
$number = function (string $name, string $unit, int $min, int $max, ?string $hint = null, bool $decimal = false) use ($values, $error): string {
    $input = $decimal
        ? '<input type="text" name="' . e($name) . '" value="' . e($values[$name]) . '" inputmode="decimal" required>'
        : '<input type="number" name="' . e($name) . '" value="' . e($values[$name]) . '" min="' . $min . '" max="' . $max . '" required>';
    return '<label class="field">' . e(t('settings.' . $name))
        . '<span class="unit-input">' . $input . '<span class="unit">' . e(t('settings.unit.' . $unit)) . '</span></span>'
        . ($hint === null ? '' : '<span class="note">' . e(t($hint)) . '</span>') . $error($name) . '</label>';
};
?>
<?= $open('settings.general') ?>
    <label class="field"><?= e(t('settings.server_name')) ?>
        <input type="text" name="server_name" value="<?= e($values['server_name']) ?>" maxlength="100" required>
        <?= $error('server_name') ?>
    </label>
    <label class="field"><?= e(t('settings.default_language')) ?>
        <select name="default_language">
            <?php foreach ($languages as $code => $name): ?>
                <option value="<?= e($code) ?>"<?= selected($code, $values['default_language']) ?>><?= e($name) ?></option>
            <?php endforeach ?>
        </select>
        <span class="note"><?= e(t('settings.default_language.hint')) ?></span>
    </label>
    <?= $number('heartbeat_interval_s', 's', 10, 3600, 'settings.heartbeat_interval_s.hint') ?>
    <?= $number('offline_after_min', 'min', 1, 10080, 'settings.offline_after_min.hint') ?>
    <?= $number('logout_after_days', 'days', 0, 365, 'settings.logout_after_days.hint') ?>
<?= $close ?>

<?= $open('settings.thresholds') ?>
    <?= $number('missed_grace_min', 'min', 1, 1440, 'settings.missed_grace_min.hint') ?>
    <?= $number('stale_days', 'days', 1, 365) ?>
    <?= $number('anomaly_drop_pct', 'pct', 1, 99) ?>
    <?= $number('anomaly_growth_factor', 'times', 0, 0, 'settings.anomaly.hint', true) ?>
    <?= $number('space_warn_days', 'days', 0, 365, 'settings.space_warn_days.hint') ?>
<?= $close ?>

<?= $open('settings.retention') ?>
    <?= $number('history_months', 'months', 1, 120) ?>
    <?= $number('log_days', 'days', 1, 3650) ?>
<?= $close ?>

<?= $open('settings.networks') ?>
    <p class="hint field-wide"><?= e(t('settings.networks.hint', ['ip' => $myIp])) ?></p>
    <label class="field"><?= e(t('settings.ip_allow_site')) ?>
        <textarea name="ip_allow_site" rows="3" class="mono" placeholder="192.168.1.0/24"><?= e($values['ip_allow_site']) ?></textarea>
        <?= $error('ip_allow_site') ?>
    </label>
    <label class="field"><?= e(t('settings.ip_allow_agents')) ?>
        <textarea name="ip_allow_agents" rows="3" class="mono" placeholder="192.168.1.0/24"><?= e($values['ip_allow_agents']) ?></textarea>
        <?= $error('ip_allow_agents') ?>
    </label>
<?= $close ?>
