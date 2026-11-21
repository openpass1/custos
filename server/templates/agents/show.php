<?php
/**
 * @var array $agent @var ?array $replaces @var list<array> $groups @var array $state @var array $credentials
 * @var array $runs @var list<array> $commands @var ?array $currentVersion @var ?array $latestSevenZip
 * @var array $settings @var list<array> $storages
 *
 * The machine page: a head with the name, the status and the buttons (one main button, rare steps under "More"),
 * a line of facts, notes that ask for something (approve, update, switched off), then problems, what runs now and
 * the tasks on the first screen. Details that are seldom needed are folded.
 * Every block that the agent changes is a live part (data-part): the page refreshes it by itself (app.js).
 */
use Custos\Domain\Agents;

$machine = $agent; // the live parts below replace $agent with the board's copy of the machine
$online = $state['online'];
$status = $agent['status'];
$approved = $status === 'approved';
$pending = $status === 'pending';
$base = '/agents/' . $agent['id'];
$name = Agents::name($agent);
$older = fn(?string $have, ?array $package) => $package !== null && ((string) $have === '' || version_compare((string) $have, $package['version'], '<'));
$agentOld = $approved && $older($agent['agent_version'], $currentVersion);
$sevenZipOld = $approved && $older($agent['sevenzip_version'], $latestSevenZip);
$groupName = null;
foreach ($groups as $g) {
    if ($agent['group_id'] !== null && (int) $agent['group_id'] === (int) $g['id']) {
        $groupName = $g['name'];
    }
}
// A button that posts to the machine (an action of AgentsController::action), with a question first when given.
$post = function (string $action, string $label, string $class, ?string $iconName = null, ?string $confirm = null) use ($base): string {
    return '<form method="post" action="' . e(url($base . '/' . $action)) . '"' . ($confirm === null ? '' : ' data-confirm="' . e($confirm) . '"') . '>'
        . csrf_field() . '<button type="submit"' . ($class === '' ? '' : ' class="' . e($class) . '"') . '>'
        . ($iconName === null ? '' : icon($iconName)) . e($label) . '</button></form>';
};
?>
<p class="crumbs"><a href="<?= e(url('/agents')) ?>"><?= e(t('agents.title')) ?></a></p>
<div data-part="head">
<div class="page-head">
    <h1><?= e($name) ?>
        <button type="button" class="icon-button" data-dialog="agent-rename" title="<?= e(t('agents.action.rename')) ?>" aria-label="<?= e(t('agents.action.rename')) ?>"><?= icon('pencil') ?></button>
        <?php if ($pending): ?><?= badge('pending') ?>
        <?php elseif ($status === 'disabled'): ?><?= badge('disabled') ?>
        <?php else: ?><?= $online ? badge('online') : badge('offline') ?><?php endif ?>
    </h1>
    <div class="page-tools">
        <?php if ($pending): ?>
            <?= $post('approve', t($replaces === null ? 'agents.action.approve' : 'agents.action.approve_new'), '', 'check') ?>
            <?php if ($replaces !== null): ?>
                <?= $post('replace', t('agents.action.replace', ['name' => Agents::name($replaces)]), 'secondary', null,
                    t('agents.confirm.replace', ['name' => Agents::name($replaces)])) ?>
            <?php endif ?>
            <?= $post('delete', t('agents.action.reject'), 'danger', null, t('agents.confirm.reject', ['name' => $name])) ?>
        <?php else: ?>
            <?php if ($approved): ?>
                <a class="button-link primary" href="<?= e(url($base . '/tasks/new')) ?>"><?= icon('plus') ?><?= e(t('agent.action.add_task')) ?></a>
                <a class="button-link" href="<?= e(url($base . '/settings')) ?>"><?= icon('sliders') ?><?= e(t('agent.action.settings')) ?></a>
            <?php else: ?>
                <?= $post('enable', t('agents.action.enable'), '', 'power') ?>
            <?php endif ?>
            <details class="dropdown">
                <summary><?= e(t('common.more')) ?><?= icon('chevron') ?></summary>
                <div class="dropdown-menu">
                    <?php if ($approved): ?>
                        <?= $post('inventory', t('agent.action.inventory'), 'menu-item', 'box') ?>
                    <?php endif ?>
                    <button type="button" class="menu-item" data-dialog="agent-rename"><?= icon('pencil') ?><?= e(t('agents.action.rename')) ?></button>
                    <button type="button" class="menu-item" data-dialog="agent-group"><?= icon('folder') ?><?= e(t('agent.action.group')) ?></button>
                    <?php if ($approved): ?>
                        <?= $post('disable', t('agents.action.disable'), 'menu-item', 'power', t('agents.confirm.disable')) ?>
                    <?php endif ?>
                    <div class="menu-sep"></div>
                    <?= $post('delete', t('agent.action.delete'), 'menu-item danger', 'trash', t('agents.confirm.delete', ['name' => $name])) ?>
                </div>
            </details>
        <?php endif ?>
    </div>
    <p class="page-meta">
        <?php if ($groupName !== null): ?><span><?= e($groupName) ?></span><?php endif ?>
        <?php if ((string) $agent['os'] !== ''): ?><span><?= e($agent['os']) ?></span><?php endif ?>
        <?php if ((string) ($agent['last_ip'] ?? '') !== ''): ?><span><?= e($agent['last_ip']) ?></span><?php endif ?>
        <?php if ((string) $agent['agent_version'] !== ''): ?><span>CUSTOS <?= e($agent['agent_version']) ?><?= Agents::edition((string) $agent['edition']) !== '' ? ' ' . e(t('agent.edition.' . $agent['edition'] . '_paren')) : '' ?></span><?php endif ?>
        <?php if ($agent['sevenzip_version'] !== null): ?><span>7-Zip <?= e($agent['sevenzip_version']) ?></span><?php endif ?>
        <?php if ($pending): ?>
            <span><?= e(t('agent.meta.first_seen', ['time' => fmt_time($agent['first_seen_at'])])) ?></span>
        <?php else: ?>
            <span><?= e(t('agent.meta.last_seen', ['ago' => fmt_ago($agent['last_heartbeat_at'])])) ?></span>
        <?php endif ?>
    </p>
</div>

<?php if ($pending): ?>
    <div class="alert alert-warn">
        <?= e(t('agents.pending.hint')) ?> <code class="fingerprint"><?= e($agent['key_fingerprint']) ?></code>
        <?php if ($replaces !== null): ?>
            <br><?= e(t('agents.rereg', ['name' => Agents::name($replaces)])) ?>.
            <a href="<?= e(url('/agents/' . $replaces['id'])) ?>"><?= e(t('agent.open_old')) ?></a>
        <?php endif ?>
    </div>
<?php elseif ($status === 'disabled'): ?>
    <div class="alert alert-muted"><?= e(t('agent.disabled.hint')) ?></div>
<?php endif ?>
<?php if ($agentOld): ?>
    <div class="alert alert-info with-action">
        <span><?= e(t('agent.update.available', ['version' => $currentVersion['version'], 'have' => (string) $agent['agent_version'] !== '' ? $agent['agent_version'] : '—'])) ?></span>
        <?= $post('update', t('agent.action.update', ['version' => $currentVersion['version']]), 'secondary', 'refresh',
            t('agent.confirm.update', ['version' => $currentVersion['version']])) ?>
    </div>
<?php endif ?>
<?php if ($sevenZipOld): ?>
    <div class="alert alert-info with-action">
        <span><?= e(t('agent.sevenzip.available', ['version' => $latestSevenZip['version'], 'have' => $agent['sevenzip_version'] ?? '—'])) ?></span>
        <?= $post('sevenzip', t('agent.action.sevenzip', ['version' => $latestSevenZip['version']]), 'secondary', 'refresh',
            t('agent.confirm.sevenzip', ['version' => $latestSevenZip['version']])) ?>
    </div>
<?php endif ?>
</div>

<div data-part="facts">
<details class="panel facts" id="agent-facts"<?= $pending ? ' open' : '' ?>>
    <summary><?= icon('chevron') ?><?= e(t('agent.facts')) ?> <small><?= e(t('agent.facts.hint')) ?></small></summary>
    <dl class="props">
        <dt><?= e(t('agent.hostname')) ?></dt><dd><?= e($agent['hostname']) ?></dd>
        <dt><?= e(t('agent.group')) ?></dt><dd><?= e($groupName ?? t('groups.none')) ?></dd>
        <dt><?= e(t('agent.ip')) ?></dt><dd><?= e($agent['last_ip'] ?? '—') ?></dd>
        <dt><?= e(t('agent.os')) ?></dt><dd><?= e($agent['os']) ?></dd>
        <dt><?= e(t('agent.version')) ?></dt><dd><?= e($agent['agent_version']) ?><?= edition_badge($agent) ?>
            <?php if (Agents::edition((string) $agent['edition']) !== ''): ?><div class="note"><?= e(t('agent.edition.' . $agent['edition'] . '_hint')) ?></div><?php endif ?></dd>
        <dt><?= e(t('agent.sevenzip')) ?></dt><dd><?= e($agent['sevenzip_version'] ?? t('agent.sevenzip.unknown')) ?></dd>
        <dt><?= e(t('agent.time_zone')) ?></dt><dd><?= e($agent['time_zone']) ?></dd>
        <dt><?= e(t('agent.first_seen')) ?></dt><dd><?= e(fmt_time($agent['first_seen_at'])) ?></dd>
        <dt><?= e(t('agent.last_seen')) ?></dt>
        <dd><?= e(fmt_time($agent['last_heartbeat_at'], true)) ?> (<?= e(fmt_ago($agent['last_heartbeat_at'])) ?>)</dd>
        <dt><?= e(t('agent.service_started')) ?></dt><dd><?= e(fmt_time($agent['service_started_at'])) ?></dd>
        <dt><?= e(t('agent.fingerprint')) ?></dt><dd><code class="fingerprint"><?= e($agent['key_fingerprint']) ?></code></dd>
        <dt><?= e(t('agent.remote_edit')) ?></dt>
        <dd><?= e($agent['allow_remote_edit'] === null ? '—' : t($agent['allow_remote_edit'] ? 'common.yes' : 'common.no')) ?></dd>
        <dt><?= e(t('agent.config')) ?></dt>
        <dd><?= $agent['config_revision'] === null ? e(t('agent.config.none'))
                : e(t('agent.config.revision', ['n' => $agent['config_revision'], 'time' => fmt_time($agent['config_received_at'])])) ?></dd>
    </dl>
    <?php if ($replaces !== null && !$pending): ?>
        <p class="note"><?= e(t('agents.rereg', ['name' => Agents::name($replaces)])) ?>
            <a href="<?= e(url('/agents/' . $replaces['id'])) ?>"><?= e(t('agent.open_old')) ?></a></p>
    <?php endif ?>
    <?php if ($credentials !== []): ?>
        <h3><?= e(t('agent.credentials')) ?></h3>
        <div class="table-wrap">
            <table>
                <thead><tr><th><?= e(t('credentials.kind')) ?></th><th><?= e(t('credentials.user')) ?></th><th><?= e(t('credentials.secret')) ?></th></tr></thead>
                <tbody>
                <?php foreach ($credentials as $c): ?>
                    <tr>
                        <td><?= e($c['kind'] ?? '') ?></td>
                        <td><?= e(trim(($c['domain'] ?? '') !== '' && ($c['domain'] ?? '') !== '.' ? $c['domain'] . '\\' . ($c['user'] ?? '') : ($c['user'] ?? ''))) ?></td>
                        <td><?= e(t(!empty($c['has_secret']) ? 'credentials.secret.set' : 'credentials.secret.none')) ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</details>
</div>

<?php extract($state); ?>
<div data-part="problems"><?php include __DIR__ . '/_problems.php'; ?></div>
<div data-part="now"><?php include __DIR__ . '/_now.php'; ?></div>
<div data-part="tasks"><?php include __DIR__ . '/_tasks.php'; ?></div>

<div data-part="storage">
<?php if ($storages !== []): ?>
    <?php include __DIR__ . '/_storage.php' ?>
<?php endif ?>
</div>

<?php /* CUSTOS for Linux: whether the volumes of the sources can have snapshots, with the fix when they cannot. */ ?>
<div data-part="snapshots">
<?php if ($snapshots !== null): ?>
    <?php include __DIR__ . '/_snapshots.php' ?>
<?php endif ?>
</div>

<div data-part="runs">
<?php if ($runs === []): ?>
    <section class="panel panel-empty">
        <h2><?= e(t('agent.runs')) ?></h2>
        <p class="empty"><?= e(t('runs.empty')) ?></p>
    </section>
<?php else: ?>
    <section class="panel">
        <h2><?= e(t('agent.runs')) ?></h2>
        <?php $showAgent = false; include __DIR__ . '/../runs/_table.php'; ?>
        <p><a href="<?= e(url('/runs', ['agent' => $agent['id']])) ?>"><?= e(t('agent.runs.all')) ?></a></p>
    </section>
<?php endif ?>
</div>

<div data-part="commands">
<?php if ($commands !== []): ?>
    <section class="panel">
        <h2><?= e(t('commands.title')) ?></h2>
        <?php $showTask = true; $back = $base; include __DIR__ . '/_commands.php'; ?>
    </section>
<?php endif ?>
</div>

<dialog id="agent-rename" class="modal" aria-labelledby="agent-rename-title">
    <form method="post" action="<?= e(url($base . '/rename')) ?>" class="stack">
        <?= csrf_field() ?>
        <h2 id="agent-rename-title"><?= e(t('agent.rename.title')) ?></h2>
        <label><?= e(t('agent.display_name')) ?>
            <input type="text" name="display_name" value="<?= e($machine['display_name'] ?? '') ?>" placeholder="<?= e($machine['hostname']) ?>" maxlength="255">
        </label>
        <p class="note"><?= e(t('agent.rename.hint', ['hostname' => $machine['hostname']])) ?></p>
        <div class="dialog-buttons">
            <button type="button" class="quiet" data-dialog-close><?= e(t('common.cancel')) ?></button>
            <button type="submit"><?= e(t('common.save')) ?></button>
        </div>
    </form>
</dialog>

<dialog id="agent-group" class="modal" aria-labelledby="agent-group-title">
    <form method="post" action="<?= e(url($base . '/group')) ?>" class="stack">
        <?= csrf_field() ?>
        <h2 id="agent-group-title"><?= e(t('agent.group.title')) ?></h2>
        <label><?= e(t('agent.group')) ?>
            <select name="group_id">
                <option value=""><?= e(t('groups.none')) ?></option>
                <?php foreach ($groups as $g): ?>
                    <option value="<?= (int) $g['id'] ?>"<?= $machine['group_id'] !== null && (int) $machine['group_id'] === (int) $g['id'] ? ' selected' : '' ?>><?= e($g['name']) ?></option>
                <?php endforeach ?>
            </select>
        </label>
        <p class="note"><a href="<?= e(url('/groups')) ?>"><?= e(t($groups === [] ? 'agent.group.create' : 'agent.group.manage')) ?></a></p>
        <div class="dialog-buttons">
            <button type="button" class="quiet" data-dialog-close><?= e(t('common.cancel')) ?></button>
            <button type="submit"><?= e(t('common.save')) ?></button>
        </div>
    </form>
</dialog>
