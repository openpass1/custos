<?php
/**
 * @var \Custos\Domain\Board $board @var list<array{name: ?string, agents: list<array>}> $sections @var list<array> $groups
 * @var string $filter @var list<array> $pending @var array $byId @var bool $hasAgents
 *
 * New machines, the quick choices and the list are live parts (data-part): the page refreshes them by itself (app.js),
 * so a machine that has just registered or sent its tasks shows up without reloading. The search and the group filter
 * are outside them and keep what was typed or chosen.
 */
use Custos\Domain\Agents;
?>
<h1><?= e(t('agents.title')) ?></h1>

<div data-part="pending">
<?php if ($pending !== []): ?>
    <section class="panel attention">
        <h2><?= e(t('agents.pending.title')) ?></h2>
        <p class="hint"><?= e(t('agents.pending.hint')) ?></p>
        <div class="table-wrap">
            <table class="cards">
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
                    <tr data-key="n<?= e($a['id']) ?>">
                        <td>
                            <a href="<?= e(url('/agents/' . $a['id'])) ?>"><?= e($a['hostname']) ?></a>
                            <?php if ($old !== null): ?>
                                <div class="note"><?= e(t('agents.rereg', ['name' => Agents::name($old)])) ?></div>
                            <?php endif ?>
                        </td>
                        <td data-label="<?= e(t('agents.col.ip')) ?>"><?= e($a['last_ip'] ?? '—') ?></td>
                        <td data-label="<?= e(t('agents.col.os')) ?>"><?= e($a['os']) ?><div class="note"><?= e(t('agents.version', ['v' => $a['agent_version']])) ?></div></td>
                        <td data-label="<?= e(t('agents.col.fingerprint')) ?>"><code class="fingerprint"><?= e($a['key_fingerprint']) ?></code></td>
                        <td data-label="<?= e(t('agents.col.first_seen')) ?>"><?= e(fmt_time($a['first_seen_at'])) ?></td>
                        <td class="actions card-end">
                            <form method="post" action="<?= e(url('/agents/' . $a['id'] . '/approve')) ?>">
                                <?= csrf_field() ?><input type="hidden" name="back" value="list">
                                <button type="submit" class="small"><?= icon('check') ?><?= e(t($old === null ? 'agents.action.approve' : 'agents.action.approve_new')) ?></button>
                            </form>
                            <?php if ($old !== null): ?>
                                <form method="post" action="<?= e(url('/agents/' . $a['id'] . '/replace')) ?>"
                                      data-confirm="<?= e(t('agents.confirm.replace', ['name' => Agents::name($old)])) ?>">
                                    <?= csrf_field() ?><input type="hidden" name="back" value="list">
                                    <button type="submit" class="small secondary"><?= e(t('agents.action.replace', ['name' => Agents::name($old)])) ?></button>
                                </form>
                            <?php endif ?>
                            <form method="post" action="<?= e(url('/agents/' . $a['id'] . '/delete')) ?>"
                                  data-confirm="<?= e(t('agents.confirm.reject', ['name' => $a['hostname']])) ?>">
                                <?= csrf_field() ?>
                                <button type="submit" class="small danger"><?= e(t('agents.action.reject')) ?></button>
                            </form>
                        </td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    </section>
<?php endif ?>
</div>

<?php if (!$hasAgents): ?>
    <div data-part="list">
        <p class="empty"><?= e(t('agents.empty')) ?> <a href="<?= e(url('/settings')) ?>"><?= e(t('agents.empty.link')) ?></a></p>
    </div>
<?php else: ?>
    <?php
    // Quick choices over the listed machines: with open problems, running a task now, offline. The search and the
    // choices work in the page (app.js); the group stays a server filter.
    $problemAgents = array_flip(array_column($board->problems, 'agent_id'));
    $flags = function (array $a) use ($board, $problemAgents): array {
        $flags = [];
        if (isset($problemAgents[$a['id']])) {
            $flags[] = 'problem';
        }
        foreach ($board->tasksOf($a['id']) as $task) {
            if (in_array($task['status'], ['running', 'paused'], true)) {
                $flags[] = 'running';
                break;
            }
        }
        if ($a['status'] === 'approved' && !$a['online']) {
            $flags[] = 'offline';
        }
        return $flags;
    };
    $chipCounts = ['' => 0, 'problem' => 0, 'running' => 0, 'offline' => 0];
    foreach ($sections as $section) {
        foreach ($section['agents'] as $a) {
            $chipCounts['']++;
            foreach ($flags($a) as $flag) {
                $chipCounts[$flag]++;
            }
        }
    }
    $dots = ['' => '', 'problem' => 'bad', 'running' => 'info', 'offline' => 'muted'];
    ?>
    <div class="list-tools" data-list-filter="agent-list">
        <div class="chips" role="group" aria-label="<?= e(t('agents.chips')) ?>" data-part="chips">
            <?php foreach ($chipCounts as $key => $n): ?>
                <button type="button" class="chip" data-filter="<?= e($key) ?>" aria-pressed="<?= $key === '' ? 'true' : 'false' ?>"<?= $n === 0 && $key !== '' ? ' disabled' : '' ?>>
                    <?php if ($dots[$key] !== ''): ?><i class="dot dot-<?= $dots[$key] ?>"></i><?php endif ?><?= e(t('agents.chip.' . ($key === '' ? 'all' : $key))) ?> <b><?= $n ?></b></button>
            <?php endforeach ?>
        </div>
        <div class="list-tools-end">
            <input type="search" class="list-search" data-list-search placeholder="<?= e(t('agents.search')) ?>" aria-label="<?= e(t('agents.search')) ?>">
            <form method="get" action="<?= e(url('/agents')) ?>" class="inline-filter">
                <label><span class="visually-hidden"><?= e(t('agents.filter.group')) ?></span>
                    <select name="group" data-autosubmit aria-label="<?= e(t('agents.filter.group')) ?>">
                        <option value=""><?= e(t('agents.filter.all_groups')) ?></option>
                        <?php foreach ($groups as $g): ?>
                            <option value="<?= (int) $g['id'] ?>"<?= $filter === (string) $g['id'] ? ' selected' : '' ?>><?= e($g['name']) ?></option>
                        <?php endforeach ?>
                        <?php if ($groups !== []): ?>
                            <option value="none"<?= $filter === 'none' ? ' selected' : '' ?>><?= e(t('groups.none')) ?></option>
                        <?php endif ?>
                    </select>
                </label>
                <noscript><button type="submit" class="secondary"><?= e(t('common.apply')) ?></button></noscript>
            </form>
            <a href="<?= e(url('/groups')) ?>"><?= e(t('groups.manage')) ?></a>
        </div>
    </div>

    <div data-part="list">
    <?php if ($sections === []): ?>
        <p class="empty"><?= e(t('agents.filter.empty')) ?></p>
    <?php else: ?>
        <section class="panel" id="agent-list">
            <div class="table-wrap">
                <table class="grid row-links cards">
                    <thead>
                    <tr>
                        <th><?= e(t('agents.col.name')) ?></th>
                        <th><?= e(t('agents.col.status')) ?></th>
                        <th><?= e(t('agents.col.last_seen')) ?></th>
                        <th><?= e(t('agents.col.version')) ?></th>
                        <th><?= e(t('agents.col.tasks')) ?></th>
                    </tr>
                    </thead>
                    <?php foreach ($sections as $section): ?>
                        <tbody data-group>
                        <?php if ($section['name'] !== null): ?>
                            <tr class="group-row"><th colspan="5"><?= e($section['name']) ?> <span class="count"><?= count($section['agents']) ?></span></th></tr>
                        <?php endif ?>
                        <?php foreach ($section['agents'] as $a): ?>
                            <?php $agentTasks = $board->tasksOf($a['id']); ?>
                            <tr data-flags="<?= e(implode(' ', $flags($a))) ?>" data-search="<?= e(mb_strtolower($a['name'] . ' ' . $a['hostname'])) ?>"
                                data-key="a<?= e($a['id']) ?>" data-sig="<?= e(implode('|', [$a['status'], $a['online'] ? 'on' : 'off', implode(',', array_column($agentTasks, 'status'))])) ?>">
                                <td><a href="<?= e(url('/agents/' . $a['id'])) ?>"><?= e($a['name']) ?></a>
                                    <?php if ($a['name'] !== $a['hostname']): ?><div class="note"><?= e($a['hostname']) ?></div><?php endif ?>
                                </td>
                                <td class="card-side">
                                    <?php if ($a['status'] === 'disabled'): ?>
                                        <?= badge('disabled') ?>
                                    <?php else: ?>
                                        <?= $a['online'] ? badge('online') : badge('offline') ?>
                                    <?php endif ?>
                                </td>
                                <td class="nowrap" data-label="<?= e(t('agents.col.last_seen')) ?>" title="<?= e(fmt_time($a['last_heartbeat_at'], true)) ?>"><?= e(fmt_ago($a['last_heartbeat_at'])) ?></td>
                                <td data-label="<?= e(t('agents.col.version')) ?>"><?= e($a['agent_version']) ?></td>
                                <td class="pills card-end">
                                    <?php if ($agentTasks === []): ?><span class="note"><?= e(t('agent.tasks.empty')) ?></span><?php endif ?>
                                    <?php foreach ($agentTasks as $task): ?>
                                        <?php $agent = $a; include __DIR__ . '/_pill.php'; ?>
                                    <?php endforeach ?>
                                </td>
                            </tr>
                        <?php endforeach ?>
                        </tbody>
                    <?php endforeach ?>
                </table>
            </div>
            <p class="empty" data-list-empty hidden><?= e(t('agents.search.empty')) ?></p>
        </section>
    <?php endif ?>
    </div>
    <p class="legend note">
        <?php foreach (['ok', 'warning', 'error', 'missed', 'running', 'missed_offline', 'stale', 'never', 'disabled'] as $s): ?>
            <span class="pill pill-<?= e(status_class($s)) ?>"><?= e(t('status.' . $s)) ?></span>
        <?php endforeach ?>
    </p>
<?php endif ?>
