<?php
/**
 * @var \Custos\Domain\Board $board @var list<array{name: ?string, agents: list<array>}> $sections @var list<array> $groups
 * @var string $filter @var list<array> $pending @var array $byId @var bool $hasAgents
 */
use Custos\Domain\Agents;
?>
<h1><?= e(t('agents.title')) ?></h1>

<?php if ($pending !== []): ?>
    <section class="panel attention">
        <h2><?= e(t('agents.pending.title')) ?></h2>
        <p class="hint"><?= e(t('agents.pending.hint')) ?></p>
        <div class="table-wrap">
            <table>
                <thead>
                <tr>
                    <th><?= e(t('agents.col.name')) ?></th>
                    <th><?= e(t('agents.col.ip')) ?></th>
                    <th><?= e(t('agents.col.os')) ?></th>
                    <th><?= e(t('agents.col.fingerprint')) ?></th>
                    <th><?= e(t('agents.col.first_seen')) ?></th>
                    <th></th>
                </tr>
                </thead>
                <tbody>
                <?php foreach ($pending as $a): ?>
                    <?php $old = $a['reregistration_of'] === null ? null : ($byId[$a['reregistration_of']] ?? null); ?>
                    <tr>
                        <td>
                            <a href="<?= e(url('/agents/' . $a['id'])) ?>"><?= e($a['hostname']) ?></a>
                            <?php if ($old !== null): ?>
                                <div class="note"><?= e(t('agents.rereg', ['name' => Agents::name($old)])) ?></div>
                            <?php endif ?>
                        </td>
                        <td><?= e($a['last_ip'] ?? '—') ?></td>
                        <td><?= e($a['os']) ?><div class="note"><?= e(t('agents.version', ['v' => $a['agent_version']])) ?></div></td>
                        <td><code class="fingerprint"><?= e($a['key_fingerprint']) ?></code></td>
                        <td><?= e(fmt_time($a['first_seen_at'])) ?></td>
                        <td class="actions">
                            <form method="post" action="<?= e(url('/agents/' . $a['id'] . '/approve')) ?>">
                                <?= csrf_field() ?><input type="hidden" name="back" value="list">
                                <button type="submit"><?= e(t($old === null ? 'agents.action.approve' : 'agents.action.approve_new')) ?></button>
                            </form>
                            <?php if ($old !== null): ?>
                                <form method="post" action="<?= e(url('/agents/' . $a['id'] . '/replace')) ?>"
                                      data-confirm="<?= e(t('agents.confirm.replace', ['name' => Agents::name($old)])) ?>">
                                    <?= csrf_field() ?><input type="hidden" name="back" value="list">
                                    <button type="submit"><?= e(t('agents.action.replace', ['name' => Agents::name($old)])) ?></button>
                                </form>
                            <?php endif ?>
                            <form method="post" action="<?= e(url('/agents/' . $a['id'] . '/delete')) ?>"
                                  data-confirm="<?= e(t('agents.confirm.reject', ['name' => $a['hostname']])) ?>">
                                <?= csrf_field() ?>
                                <button type="submit" class="danger"><?= e(t('agents.action.reject')) ?></button>
                            </form>
                        </td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    </section>
<?php endif ?>

<?php if (!$hasAgents): ?>
    <p class="empty"><?= e(t('agents.empty')) ?> <a href="<?= e(url('/settings')) ?>"><?= e(t('agents.empty.link')) ?></a></p>
<?php else: ?>
    <form method="get" action="<?= e(url('/agents')) ?>" class="filters">
        <label><?= e(t('agents.filter.group')) ?>
            <select name="group" data-autosubmit>
                <option value=""><?= e(t('common.all')) ?></option>
                <?php foreach ($groups as $g): ?>
                    <option value="<?= (int) $g['id'] ?>"<?= $filter === (string) $g['id'] ? ' selected' : '' ?>><?= e($g['name']) ?></option>
                <?php endforeach ?>
                <?php if ($groups !== []): ?>
                    <option value="none"<?= $filter === 'none' ? ' selected' : '' ?>><?= e(t('groups.none')) ?></option>
                <?php endif ?>
            </select>
        </label>
        <noscript><button type="submit"><?= e(t('common.apply')) ?></button></noscript>
        <a href="<?= e(url('/groups')) ?>"><?= e(t('groups.manage')) ?></a>
    </form>

    <?php if ($sections === []): ?>
        <p class="empty"><?= e(t('agents.filter.empty')) ?></p>
    <?php endif ?>
    <?php foreach ($sections as $section): ?>
        <?php if ($section['name'] !== null): ?><h2 class="group-title"><?= e($section['name']) ?></h2><?php endif ?>
        <div class="table-wrap">
            <table class="grid">
                <thead>
                <tr>
                    <th><?= e(t('agents.col.name')) ?></th>
                    <th><?= e(t('agents.col.status')) ?></th>
                    <th><?= e(t('agents.col.last_seen')) ?></th>
                    <th><?= e(t('agents.col.version')) ?></th>
                    <th><?= e(t('agents.col.tasks')) ?></th>
                </tr>
                </thead>
                <tbody>
                <?php foreach ($section['agents'] as $a): ?>
                    <tr>
                        <td><a href="<?= e(url('/agents/' . $a['id'])) ?>"><?= e($a['name']) ?></a>
                            <?php if ($a['name'] !== $a['hostname']): ?><div class="note"><?= e($a['hostname']) ?></div><?php endif ?>
                        </td>
                        <td>
                            <?php if ($a['status'] === 'disabled'): ?>
                                <?= badge('disabled') ?>
                            <?php else: ?>
                                <?= $a['online'] ? badge('online') : badge('offline') ?>
                            <?php endif ?>
                        </td>
                        <td class="nowrap" title="<?= e(fmt_time($a['last_heartbeat_at'], true)) ?>"><?= e(fmt_ago($a['last_heartbeat_at'])) ?></td>
                        <td><?= e($a['agent_version']) ?></td>
                        <td class="pills">
                            <?php $agentTasks = $board->tasksOf($a['id']); ?>
                            <?php if ($agentTasks === []): ?><span class="note"><?= e(t('agent.tasks.empty')) ?></span><?php endif ?>
                            <?php foreach ($agentTasks as $task): ?>
                                <?php $agent = $a; include __DIR__ . '/_pill.php'; ?>
                            <?php endforeach ?>
                        </td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endforeach ?>
    <p class="legend note">
        <?php foreach (['ok', 'warning', 'error', 'missed', 'running', 'missed_offline', 'stale', 'never', 'disabled'] as $s): ?>
            <span class="pill pill-<?= e(status_class($s)) ?>"><?= e(t('status.' . $s)) ?></span>
        <?php endforeach ?>
    </p>
<?php endif ?>
