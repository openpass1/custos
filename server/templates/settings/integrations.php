<?php
/**
 * @var string $list @var ?string $error @var string $base @var string $zabbixToken @var string $wallToken @var string $myIp
 * @var string $cronLast
 */
$wallUrl = $base . '/wall?token=' . rawurlencode($wallToken);
?>
<h1><?= e(t('settings.title')) ?></h1>
<nav class="tabs">
    <a href="<?= e(url('/settings')) ?>"><?= e(t('settings.tab.general')) ?></a>
    <a href="<?= e(url('/settings/notifications')) ?>"><?= e(t('settings.tab.notifications')) ?></a>
    <a class="active" href="<?= e(url('/settings/integrations')) ?>"><?= e(t('integrations.title')) ?></a>
    <a href="<?= e(url('/groups')) ?>"><?= e(t('groups.title')) ?></a>
</nav>

<section class="panel">
    <h2><?= e(t('integrations.wall')) ?></h2>
    <p class="hint"><?= e(t('integrations.wall.hint')) ?></p>
    <dl class="props">
        <dt><?= e(t('integrations.wall.url')) ?></dt>
        <dd>
            <span class="copy-row">
                <input type="text" id="wall-url" class="mono" value="<?= e($wallUrl) ?>" readonly size="60">
                <button type="button" data-copy="wall-url"><?= e(t('common.copy')) ?></button>
                <a class="button-link" href="<?= e($wallUrl) ?>" target="_blank" rel="noopener"><?= e(t('integrations.wall.open')) ?></a>
            </span>
        </dd>
    </dl>
    <form method="post" action="<?= e(url('/settings/integrations/wall-token')) ?>" data-confirm="<?= e(t('integrations.confirm.wall')) ?>">
        <?= csrf_field() ?>
        <button type="submit" class="secondary"><?= e(t('integrations.action.token')) ?></button>
        <span class="note"><?= e(t('integrations.token.hint')) ?></span>
    </form>
</section>

<section class="panel">
    <h2><?= e(t('integrations.zabbix')) ?></h2>
    <p class="hint"><?= e(t('integrations.zabbix.hint')) ?></p>
    <dl class="props">
        <dt><?= e(t('integrations.zabbix.url_macro')) ?></dt>
        <dd><code>{$CUSTOS.URL}</code> = <code><?= e($base) ?></code></dd>
        <dt><?= e(t('integrations.zabbix.token_macro')) ?></dt>
        <dd>
            <code>{$CUSTOS.TOKEN}</code> =
            <span class="copy-row">
                <input type="text" id="zabbix-token" class="mono" value="<?= e($zabbixToken) ?>" readonly size="52">
                <button type="button" data-copy="zabbix-token"><?= e(t('common.copy')) ?></button>
            </span>
        </dd>
        <dt><?= e(t('integrations.zabbix.template')) ?></dt>
        <dd><a href="<?= e(url('/settings/integrations/zabbix-template')) ?>">custos_template.yaml</a>
            <span class="note"><?= e(t('integrations.zabbix.template.hint')) ?></span></dd>
        <dt><?= e(t('integrations.zabbix.endpoints')) ?></dt>
        <dd class="mono">
            <?= e($base) ?>/api/zabbix/status<br>
            <?= e($base) ?>/api/zabbix/discovery/agents<br>
            <?= e($base) ?>/api/zabbix/discovery/tasks<br>
            <?= e($base) ?>/api/health <span class="note"><?= e(t('integrations.zabbix.health')) ?></span>
        </dd>
        <dt><?= e(t('integrations.cron')) ?></dt>
        <dd><?= e($cronLast === '' ? t('dashboard.cron.never') : fmt_time($cronLast, true) . ' (' . fmt_ago($cronLast) . ')') ?></dd>
    </dl>
    <form method="post" action="<?= e(url('/settings/integrations/zabbix-token')) ?>" data-confirm="<?= e(t('integrations.confirm.zabbix')) ?>">
        <?= csrf_field() ?>
        <button type="submit" class="secondary"><?= e(t('integrations.action.token')) ?></button>
        <span class="note"><?= e(t('integrations.token.hint')) ?></span>
    </form>
</section>

<form method="post" action="<?= e(url('/settings/integrations')) ?>" class="panel stack">
    <?= csrf_field() ?>
    <h2><?= e(t('integrations.networks')) ?></h2>
    <p class="hint"><?= e(t('integrations.networks.hint', ['ip' => $myIp])) ?></p>
    <label><?= e(t('settings.ip_allow_integrations')) ?>
        <textarea name="ip_allow_integrations" rows="3" class="mono" placeholder="192.168.1.0/24"><?= e($list) ?></textarea>
    </label>
    <?php if ($error !== null): ?><div class="field-error"><?= e($error) ?></div><?php endif ?>
    <button type="submit"><?= e(t('common.save')) ?></button>
</form>
