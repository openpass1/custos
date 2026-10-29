<?php
/**
 * @var array $agent @var ?array $replaces @var bool $online @var array $tasks @var ?array $progress
 * @var ?array $running @var array $queue @var array $credentials @var array $runs
 */
use Custos\Domain\Agents;

$taskNames = array_column($tasks, 'name', 'id');
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
            <dt><?= e(t('agent.ip')) ?></dt><dd><?= e($agent['last_ip'] ?? '—') ?></dd>
            <dt><?= e(t('agent.os')) ?></dt><dd><?= e($agent['os']) ?></dd>
            <dt><?= e(t('agent.version')) ?></dt><dd><?= e($agent['agent_version']) ?></dd>
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
                <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/disable')) ?>"
                      data-confirm="<?= e(t('agents.confirm.disable')) ?>">
                    <?= csrf_field() ?>
                    <button type="submit"><?= e(t('agents.action.disable')) ?></button>
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

<section class="panel">
    <h2><?= e(t('agent.now')) ?></h2>
    <?php if ($running === null): ?>
        <p><?= e($agent['state'] === null ? '—' : t('agent.state.idle')) ?></p>
    <?php else: ?>
        <p>
            <?= badge($agent['state'] === 'paused' ? 'paused' : 'running') ?>
            <?= e($taskNames[$running['task_id']] ?? $running['task_id']) ?>
            <?php if (!empty($running['paused_at'])): ?>
                · <?= e(t('agent.paused_since', ['time' => fmt_time(\Custos\Infra\Time::parseIso($running['paused_at']))])) ?>
            <?php endif ?>
        </p>
        <?php if ($progress !== null && $progress['run_id'] === $running['run_id']): ?>
            <progress max="100" value="<?= e(min(100, (float) $progress['percent'])) ?>"></progress>
            <p class="note">
                <?= e(t('phase.' . $progress['phase'])) ?> · <?= e(round((float) $progress['percent'], 1)) ?>%
                · <?= e(fmt_number($progress['files_done'])) ?> / <?= e(fmt_number($progress['files_total'])) ?> <?= e(t('agent.files')) ?>
                · <?= e(fmt_bytes($progress['speed_bps'])) ?>/<?= e(t('unit.s')) ?>
                · <?= e(t('agent.eta', ['time' => fmt_duration($progress['eta_s'])])) ?>
            </p>
            <?php if ($progress['current_file'] !== null): ?><p class="note mono"><?= e($progress['current_file']) ?></p><?php endif ?>
        <?php endif ?>
    <?php endif ?>
    <?php if ($queue !== []): ?>
        <p class="note"><?= e(t('agent.queue')) ?>: <?= e(implode(', ', array_map(fn($id) => $taskNames[$id] ?? $id, $queue))) ?></p>
    <?php endif ?>
</section>

<section class="panel">
    <h2><?= e(t('agent.tasks')) ?></h2>
    <?php if ($tasks === []): ?>
        <p class="empty"><?= e(t('agent.tasks.empty')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead>
                <tr>
                    <th><?= e(t('tasks.col.name')) ?></th>
                    <th><?= e(t('tasks.col.type')) ?></th>
                    <th><?= e(t('tasks.col.schedule')) ?></th>
                    <th><?= e(t('tasks.col.last_run')) ?></th>
                    <th><?= e(t('tasks.col.last_success')) ?></th>
                    <th><?= e(t('tasks.col.revision')) ?></th>
                    <th></th>
                </tr>
                </thead>
                <tbody>
                <?php foreach ($tasks as $task): ?>
                    <?php $data = json_decode($task['data'], true) ?: []; ?>
                    <tr>
                        <td><?= e($task['name']) ?><?php if (!$task['enabled']): ?> <?= badge('disabled') ?><?php endif ?></td>
                        <td><?= e(t('backup.' . ($data['backup_type'] ?? ''))) ?></td>
                        <td><?= e(schedule_label($data['schedule'] ?? null)) ?></td>
                        <td>
                            <?php if ($task['last_run_id'] !== null): ?>
                                <a href="<?= e(url('/runs/' . $task['last_run_id'])) ?>"><?= badge($task['last_run_status']) ?></a>
                                <?= e(fmt_time($task['last_run_at'])) ?>
                            <?php else: ?>
                                <?= e(t('tasks.never')) ?>
                            <?php endif ?>
                        </td>
                        <td><?= e(fmt_time($task['last_success_at'])) ?></td>
                        <td class="note" title="<?= e($task['updated_by'] ?? '') ?>"><?= (int) $task['revision'] ?> · <?= e(fmt_time($task['updated_at'])) ?></td>
                        <td><a href="<?= e(url('/runs', ['agent' => $agent['id'], 'task' => $task['id']])) ?>"><?= e(t('tasks.history')) ?></a></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>

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
