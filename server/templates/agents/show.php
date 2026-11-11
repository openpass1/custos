<?php
/**
 * @var array $agent @var ?array $replaces @var list<array> $groups @var array $live @var array $credentials
 * @var array $runs @var int $refresh @var list<array> $commands @var ?array $currentVersion @var ?array $latestSevenZip
 * @var array $settings @var list<array> $storages
 */
use Custos\Domain\Agents;

$online = $live['online'];
$approved = $agent['status'] === 'approved';
$older = fn(?string $have, ?array $package) => $package !== null && ((string) $have === '' || version_compare((string) $have, $package['version'], '<'));
$agentOld = $older($agent['agent_version'], $currentVersion);
$sevenZipOld = $older($agent['sevenzip_version'], $latestSevenZip);
?>
<p class="crumbs"><a href="<?= e(url('/agents')) ?>"><?= e(t('agents.title')) ?></a></p>
<h1><?= e(Agents::name($agent)) ?>
    <?php if ($agent['status'] === 'pending'): ?><?= badge('pending') ?>
    <?php elseif ($agent['status'] === 'disabled'): ?><?= badge('disabled') ?>
    <?php else: ?><?= $online ? badge('online') : badge('offline') ?><?php endif ?>
</h1>

<div class="columns">
    <section class="panel">
        <h2><?= e(t('agent.card')) ?></h2>
        <dl class="props">
            <dt><?= e(t('agent.hostname')) ?></dt><dd><?= e($agent['hostname']) ?></dd>
            <dt><?= e(t('agent.group')) ?></dt>
            <dd>
                <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/group')) ?>" class="inline">
                    <?= csrf_field() ?>
                    <select name="group_id" aria-label="<?= e(t('agent.group')) ?>" data-autosubmit>
                        <option value=""><?= e(t('groups.none')) ?></option>
                        <?php foreach ($groups as $g): ?>
                            <option value="<?= (int) $g['id'] ?>"<?= (int) $agent['group_id'] === (int) $g['id'] && $agent['group_id'] !== null ? ' selected' : '' ?>><?= e($g['name']) ?></option>
                        <?php endforeach ?>
                    </select>
                    <noscript><button type="submit"><?= e(t('common.save')) ?></button></noscript>
                    <?php if ($groups === []): ?><a href="<?= e(url('/groups')) ?>" class="note"><?= e(t('groups.create_first')) ?></a><?php endif ?>
                </form>
            </dd>
            <dt><?= e(t('agent.ip')) ?></dt><dd><?= e($agent['last_ip'] ?? '—') ?></dd>
            <dt><?= e(t('agent.os')) ?></dt><dd><?= e($agent['os']) ?></dd>
            <dt><?= e(t('agent.version')) ?></dt><dd><?= e($agent['agent_version']) ?>
                <?php if ($agentOld && (string) $agent['agent_version'] !== ''): ?><?= badge('warning', t('agent.version.old', ['version' => $currentVersion['version']])) ?><?php endif ?></dd>
            <dt><?= e(t('agent.sevenzip')) ?></dt><dd><?= e($agent['sevenzip_version'] ?? t('agent.sevenzip.unknown')) ?>
                <?php if ($sevenZipOld && $agent['sevenzip_version'] !== null): ?><?= badge('warning', t('agent.version.old', ['version' => $latestSevenZip['version']])) ?><?php endif ?></dd>
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
        <?php if ($replaces !== null): ?>
            <p class="note"><?= e(t('agents.rereg', ['name' => Agents::name($replaces)])) ?>
                <a href="<?= e(url('/agents/' . $replaces['id'])) ?>"><?= e(t('agent.open_old')) ?></a></p>
        <?php endif ?>
    </section>

    <section class="panel">
        <h2><?= e(t('agent.actions')) ?></h2>
        <div class="stack">
            <?php if ($agent['status'] === 'pending'): ?>
                <p class="hint"><?= e(t('agents.pending.hint')) ?></p>
                <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/approve')) ?>">
                    <?= csrf_field() ?>
                    <button type="submit"><?= e(t($replaces === null ? 'agents.action.approve' : 'agents.action.approve_new')) ?></button>
                </form>
                <?php if ($replaces !== null): ?>
                    <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/replace')) ?>"
                          data-confirm="<?= e(t('agents.confirm.replace', ['name' => Agents::name($replaces)])) ?>">
                        <?= csrf_field() ?>
                        <button type="submit"><?= e(t('agents.action.replace', ['name' => Agents::name($replaces)])) ?></button>
                    </form>
                <?php endif ?>
            <?php elseif ($agent['status'] === 'approved'): ?>
                <div class="buttons">
                    <a class="button-link" href="<?= e(url('/agents/' . $agent['id'] . '/tasks/new')) ?>"><?= e(t('agent.action.add_task')) ?></a>
                    <a class="button-link" href="<?= e(url('/agents/' . $agent['id'] . '/settings')) ?>"><?= e(t('agent.action.settings')) ?></a>
                    <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/inventory')) ?>">
                        <?= csrf_field() ?>
                        <button type="submit" class="secondary"><?= e(t('agent.action.inventory')) ?></button>
                    </form>
                </div>
                <?php if ($agentOld || $sevenZipOld): ?>
                    <div class="buttons">
                        <?php if ($agentOld): ?>
                            <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/update')) ?>"
                                  data-confirm="<?= e(t('agent.confirm.update', ['version' => $currentVersion['version']])) ?>">
                                <?= csrf_field() ?>
                                <button type="submit"><?= e(t('agent.action.update', ['version' => $currentVersion['version']])) ?></button>
                            </form>
                        <?php endif ?>
                        <?php if ($sevenZipOld): ?>
                            <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/sevenzip')) ?>"
                                  data-confirm="<?= e(t('agent.confirm.sevenzip', ['version' => $latestSevenZip['version']])) ?>">
                                <?= csrf_field() ?>
                                <button type="submit"><?= e(t('agent.action.sevenzip', ['version' => $latestSevenZip['version']])) ?></button>
                            </form>
                        <?php endif ?>
                    </div>
                <?php endif ?>
                <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/disable')) ?>"
                      data-confirm="<?= e(t('agents.confirm.disable')) ?>">
                    <?= csrf_field() ?>
                    <button type="submit" class="secondary"><?= e(t('agents.action.disable')) ?></button>
                </form>
            <?php else: ?>
                <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/enable')) ?>">
                    <?= csrf_field() ?>
                    <button type="submit"><?= e(t('agents.action.enable')) ?></button>
                </form>
            <?php endif ?>
            <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/rename')) ?>" class="inline">
                <?= csrf_field() ?>
                <input type="text" name="display_name" value="<?= e($agent['display_name'] ?? '') ?>"
                       placeholder="<?= e($agent['hostname']) ?>" maxlength="255" aria-label="<?= e(t('agent.display_name')) ?>">
                <button type="submit"><?= e(t('agents.action.rename')) ?></button>
            </form>
            <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/delete')) ?>"
                  data-confirm="<?= e(t('agents.confirm.delete', ['name' => Agents::name($agent)])) ?>">
                <?= csrf_field() ?>
                <button type="submit" class="danger"><?= e(t($agent['status'] === 'pending' ? 'agents.action.reject' : 'agents.action.delete')) ?></button>
            </form>
        </div>
    </section>
</div>

<div data-live="<?= e(url('/agents/' . $agent['id'] . '/live')) ?>" data-live-every="<?= (int) $refresh ?>">
    <div data-part="problems"><?php extract($live); include __DIR__ . '/_problems.php'; ?></div>
    <div data-part="now"><?php include __DIR__ . '/_now.php'; ?></div>
    <?php if ($storages !== []): ?>
        <?php /* Not a live part: the inventory changes after runs, not every few seconds. */ include __DIR__ . '/_storage.php' ?>
    <?php endif ?>
    <div data-part="tasks"><?php include __DIR__ . '/_tasks.php'; ?></div>
</div>

<?php if ($commands !== []): ?>
    <section class="panel">
        <h2><?= e(t('commands.title')) ?></h2>
        <?php $showTask = true; $back = '/agents/' . $agent['id']; include __DIR__ . '/_commands.php'; ?>
    </section>
<?php endif ?>

<section class="panel">
    <h2><?= e(t('agent.runs')) ?></h2>
    <?php if ($runs === []): ?>
        <p class="empty"><?= e(t('runs.empty')) ?></p>
    <?php else: ?>
        <?php $showAgent = false; include __DIR__ . '/../runs/_table.php'; ?>
        <p><a href="<?= e(url('/runs', ['agent' => $agent['id']])) ?>"><?= e(t('agent.runs.all')) ?></a></p>
    <?php endif ?>
</section>

<?php if ($credentials !== []): ?>
    <section class="panel">
        <h2><?= e(t('agent.credentials')) ?></h2>
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
    </section>
<?php endif ?>
