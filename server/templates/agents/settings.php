<?php
/**
 * @var array $agent @var array $values @var array<string, string> $errors @var bool $known @var ?array $pending
 * @var ?string $refusal
 */
use Custos\Domain\Agents;

$error = fn(string $name) => isset($errors[$name]) ? '<div class="field-error">' . e($errors[$name]) . '</div>' : '';
$reported = json_decode((string) $agent['settings'], true) ?: [];
$passwordSet = !empty($reported['program_password_hash']);
$action = $values['password_action'] ?? 'keep';
?>
<p class="crumbs"><a href="<?= e(url('/agents')) ?>"><?= e(t('agents.title')) ?></a> ›
    <a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e(Agents::name($agent)) ?></a></p>
<h1><?= e(t('agent_settings.title', ['name' => Agents::name($agent)])) ?></h1>
<p class="hint"><?= e(t('agent_settings.hint')) ?></p>
<?php if ($refusal !== null): ?>
    <div class="alert alert-warn"><?= e($refusal) ?></div>
<?php endif ?>
<?php if (!$known): ?>
    <div class="alert alert-warn"><?= e(t('agent_settings.unknown')) ?></div>
<?php endif ?>
<?php if ($pending !== null): ?>
    <div class="alert alert-info"><?= e(t('agent_settings.pending', ['time' => fmt_time($pending['created_at']), 'by' => (string) $pending['created_by_login']])) ?>
        <?= badge($pending['status']) ?></div>
<?php endif ?>
<?= $error('agent') ?>

<form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/settings')) ?>" class="stack" autocomplete="off">
    <?= csrf_field() ?>
    <fieldset class="editor" data-scope<?= $refusal !== null ? ' disabled' : '' ?>>
        <section class="panel stack">
            <h2><?= e(t('agent_settings.password')) ?></h2>
            <p class="note"><?= e(t($passwordSet ? 'agent_settings.password.state.set' : ($known ? 'agent_settings.password.state.none' : 'agent_settings.password.state.unknown'))) ?></p>
            <div class="choices">
                <?php foreach (['keep', 'set', 'reset'] as $k): ?>
                    <label class="choice"><input type="radio" name="password_action" value="<?= $k ?>"<?= checked($action === $k) ?>>
                        <span><?= e(t('agent_settings.password.' . $k)) ?></span></label>
                <?php endforeach ?>
            </div>
            <div class="row-fields" data-show-if="password_action=set">
                <label><?= e(t('agent_settings.password.new')) ?>
                    <input type="password" name="program_password" value="" autocomplete="new-password"></label>
                <label><?= e(t('agent_settings.password.again')) ?>
                    <input type="password" name="program_password_again" value="" autocomplete="new-password"></label>
            </div>
            <?= $error('program_password') ?>
            <p class="note"><?= e(t('agent_settings.password.hint')) ?></p>
        </section>

        <section class="panel stack">
            <h2><?= e(t('agent_settings.program')) ?></h2>
            <label><?= e(t('agent_settings.tray_mode')) ?>
                <select name="tray_mode">
                    <?php foreach (['full', 'status', 'hidden'] as $k): ?>
                        <option value="<?= $k ?>"<?= selected($k, $values['tray_mode']) ?>><?= e(t('agent_settings.tray_mode.' . $k)) ?></option>
                    <?php endforeach ?>
                </select>
            </label>
            <label class="check"><input type="checkbox" name="allow_all_users_edit" value="1"<?= checked((bool) $values['allow_all_users_edit']) ?>>
                <?= e(t('agent_settings.allow_all_users_edit')) ?></label>
            <p class="note"><?= e(t('agent_settings.allow_all_users_edit.hint')) ?></p>
            <label><?= e(t('agent_settings.log_retention_days')) ?>
                <input type="number" name="log_retention_days" value="<?= e((string) $values['log_retention_days']) ?>" min="1" max="3650" class="short">
            </label>
            <?= $error('log_retention_days') ?>
            <label><?= e(t('agent_settings.default_log_level')) ?>
                <select name="default_log_level">
                    <?php foreach (['summary', 'files'] as $k): ?>
                        <option value="<?= $k ?>"<?= selected($k, $values['default_log_level']) ?>><?= e(t('task_form.log_level.' . $k)) ?></option>
                    <?php endforeach ?>
                </select>
            </label>
            <label><?= e(t('agent_settings.process_priority')) ?>
                <select name="process_priority">
                    <?php foreach (['low', 'normal'] as $k): ?>
                        <option value="<?= $k ?>"<?= selected($k, $values['process_priority']) ?>><?= e(t('agent_settings.process_priority.' . $k)) ?></option>
                    <?php endforeach ?>
                </select>
            </label>
        </section>
        <?php if ($refusal === null): ?>
            <div class="buttons">
                <button type="submit"><?= e(t('agent_settings.submit')) ?></button>
                <a href="<?= e(url('/agents/' . $agent['id'])) ?>"><?= e(t('task_form.cancel')) ?></a>
                <span class="note"><?= e(t('agent_settings.submit.hint')) ?></span>
            </div>
        <?php endif ?>
    </fieldset>
</form>
