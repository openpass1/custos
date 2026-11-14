<?php
/**
 * @var array $tpl @var array<string, array> $links by agent id @var list<array> $agents @var list<array> $groups
 * @var list<array> $accounts accounts the template needs
 */
use Custos\Domain\Agents;

$outdated = count(array_filter($links, fn($l) => (int) $l['template_revision'] < (int) $tpl['revision']));
?>
<p class="crumbs"><a href="<?= e(url('/templates')) ?>"><?= e(t('templates.title')) ?></a></p>
<div class="page-head">
    <h1><?= e($tpl['name']) ?></h1>
    <div class="page-tools">
        <a class="button-link" href="<?= e(url('/templates/' . $tpl['id'] . '/edit')) ?>"><?= icon('pencil') ?><?= e(t('templates.action.edit')) ?></a>
        <form method="post" action="<?= e(url('/templates/' . $tpl['id'] . '/delete')) ?>"
              data-confirm="<?= e(t('templates.confirm.delete', ['name' => $tpl['name']])) ?>">
            <?= csrf_field() ?>
            <button type="submit" class="danger"><?= icon('trash') ?><?= e(t('templates.action.delete')) ?></button>
        </form>
    </div>
    <p class="page-meta"><span><?= e(task_summary($tpl['task'])) ?></span>
        <span><?= e(t('templates.revision', ['n' => (int) $tpl['revision'], 'time' => fmt_time($tpl['updated_at']), 'by' => $tpl['updated_by']])) ?></span></p>
</div>

<div class="columns">
<section class="panel">
    <h2><?= e(t('templates.apply')) ?></h2>
    <p class="hint"><?= e(t('templates.apply.hint')) ?></p>
    <form method="post" action="<?= e(url('/templates/' . $tpl['id'] . '/apply')) ?>" class="stack" autocomplete="off" data-scope>
        <?= csrf_field() ?>
        <div class="choices">
            <label class="choice"><input type="radio" name="target" value="selected" checked> <span><?= e(t('templates.target.selected')) ?></span></label>
            <label class="choice"><input type="radio" name="target" value="group"> <span><?= e(t('templates.target.group')) ?></span></label>
            <?php if ($links !== []): ?>
                <label class="choice"><input type="radio" name="target" value="linked"> <span><?= e(t('templates.target.linked', ['n' => count($links)])) ?></span></label>
            <?php endif ?>
        </div>
        <div data-show-if="target=selected" class="machine-list">
            <?php if ($agents === []): ?><p class="empty"><?= e(t('agents.title')) ?>: 0</p><?php endif ?>
            <?php foreach ($agents as $a): ?>
                <label class="check"><input type="checkbox" name="agent_ids[]" value="<?= e($a['id']) ?>"<?= $a['status'] !== 'approved' ? ' disabled' : '' ?>>
                    <?= e(Agents::name($a)) ?>
                    <?php if ($a['group_id'] !== null): ?><span class="note"><?= e((string) ($a['group_name'] ?? '')) ?></span><?php endif ?>
                    <?php if ($a['status'] !== 'approved'): ?><?= badge($a['status']) ?><?php endif ?>
                    <?php if (isset($links[$a['id']])): ?><span class="note"><?= e(t('templates.linked', ['n' => (int) $links[$a['id']]['template_revision']])) ?></span><?php endif ?>
                </label>
            <?php endforeach ?>
        </div>
        <label data-show-if="target=group"><?= e(t('agent.group')) ?>
            <select name="group_id">
                <?php foreach ($groups as $g): ?>
                    <option value="<?= (int) $g['id'] ?>"><?= e($g['name']) ?></option>
                <?php endforeach ?>
                <option value="none"><?= e(t('groups.none')) ?></option>
            </select>
        </label>
        <?php if ($accounts !== []): ?>
            <h3><?= e(t('templates.passwords')) ?></h3>
            <p class="note"><?= e(t('templates.passwords.hint')) ?></p>
            <?php foreach ($accounts as $c): ?>
                <label><?= e(account_label($c)) ?>
                    <input type="password" name="passwords[<?= e($c['id']) ?>]" value="" autocomplete="new-password">
                </label>
            <?php endforeach ?>
        <?php endif ?>
        <button type="submit"><?= e(t('templates.apply.submit')) ?></button>
    </form>
</section>

<?php /* Live part: whether the agents took the template shows up by itself (app.js). */ ?>
<section class="panel" data-part="machines">
    <h2><?= e(t('templates.machines')) ?>
        <?php if ($outdated > 0): ?><?= badge('warning', t('templates.outdated', ['n' => $outdated])) ?><?php endif ?></h2>
    <?php if ($links === []): ?>
        <p class="empty"><?= e(t('templates.machines.none')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table>
                <thead><tr><th><?= e(t('runs.col.agent')) ?></th><th><?= e(t('templates.col.revision')) ?></th><th><?= e(t('templates.col.state')) ?></th></tr></thead>
                <tbody>
                <?php foreach ($links as $l): ?>
                    <tr>
                        <td><a href="<?= e(url('/agents/' . $l['agent_id'] . '/tasks/' . $l['task_id'])) ?>"><?= e(Agents::name($l)) ?></a>
                            <div class="note"><?= e(fmt_time($l['applied_at'])) ?> · <?= e($l['applied_by']) ?></div></td>
                        <td><?= (int) $l['template_revision'] ?>
                            <?php if ((int) $l['template_revision'] < (int) $tpl['revision']): ?><?= badge('warning', t('templates.old')) ?><?php endif ?></td>
                        <td><?php if ($l['command_status'] !== null): ?><?= badge($l['command_status']) ?><?php endif ?>
                            <?php if ($l['task_deleted']): ?><?= badge('deleted', t('tasks.deleted')) ?><?php endif ?>
                            <?php if ((string) $l['result_message'] !== ''): ?><div class="note"><?= e((string) $l['result_message']) ?></div><?php endif ?></td>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
    <?php endif ?>
</section>
</div>
