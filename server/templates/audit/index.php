<?php /** @var array $rows @var array $filter @var array $users @var int $page @var int $pages */ ?>
<h1><?= e(t('audit.title')) ?></h1>

<form method="get" action="<?= e(url('/audit')) ?>" class="filters">
    <label><?= e(t('audit.col.user')) ?>
        <select name="user">
            <option value=""><?= e(t('common.all')) ?></option>
            <?php foreach ($users as $u): ?>
                <option value="<?= (int) $u['id'] ?>"<?= $filter['user'] === (string) $u['id'] ? ' selected' : '' ?>><?= e($u['login']) ?></option>
            <?php endforeach ?>
        </select>
    </label>
    <label><?= e(t('common.from')) ?> <input type="date" name="from" value="<?= e($filter['from']) ?>"></label>
    <label><?= e(t('common.to')) ?> <input type="date" name="to" value="<?= e($filter['to']) ?>"></label>
    <button type="submit"><?= e(t('common.apply')) ?></button>
    <?php if (array_filter($filter) !== []): ?><a href="<?= e(url('/audit')) ?>"><?= e(t('common.reset')) ?></a><?php endif ?>
</form>

<?php if ($rows === []): ?>
    <p class="empty"><?= e(t('audit.empty')) ?></p>
<?php else: ?>
    <div class="table-wrap">
        <table>
            <thead>
            <tr>
                <th><?= e(t('audit.col.time')) ?></th>
                <th><?= e(t('audit.col.user')) ?></th>
                <th><?= e(t('audit.col.ip')) ?></th>
                <th><?= e(t('audit.col.action')) ?></th>
                <th><?= e(t('audit.col.details')) ?></th>
            </tr>
            </thead>
            <tbody>
            <?php foreach ($rows as $row): ?>
                <?php $details = json_decode((string) $row['details'], true) ?: []; ?>
                <tr<?= in_array($row['action'], ['login.failed', 'login.blocked'], true) ? ' class="warn-row"' : '' ?>>
                    <td><?= e(fmt_time($row['created_at'], true)) ?></td>
                    <td><?= e($row['login'] !== '' ? $row['login'] : '—') ?></td>
                    <td><?= e($row['ip']) ?></td>
                    <td><?= e(t('audit.action.' . $row['action'])) ?></td>
                    <td class="note">
                        <?php if ($row['object_type'] === 'agent' && $row['object_id'] !== null && $row['action'] !== 'agent.delete'): ?>
                            <a href="<?= e(url('/agents/' . $row['object_id'])) ?>"><?= e($details['name'] ?? $row['object_id']) ?></a>
                            <?php unset($details['name']); ?>
                        <?php endif ?>
                        <?php foreach ($details as $key => $value): ?>
                            <span><?= e($key) ?>: <?= e(is_scalar($value) ? (string) $value : json_encode($value, JSON_UNESCAPED_UNICODE)) ?></span>
                        <?php endforeach ?>
                    </td>
                </tr>
            <?php endforeach ?>
            </tbody>
        </table>
    </div>
    <?= pager($page, $pages, '/audit', $filter) ?>
<?php endif ?>
