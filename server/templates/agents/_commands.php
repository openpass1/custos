<?php
/** @var array $agent @var list<array> $commands @var bool $showTask @var string $back the page to return to after "cancel" */
?>
<div class="table-wrap">
    <table>
        <thead><tr>
            <th><?= e(t('commands.col.created')) ?></th>
            <th><?= e(t('commands.col.command')) ?></th>
            <th><?= e(t('commands.col.status')) ?></th>
            <th><?= e(t('commands.col.result')) ?></th>
            <th></th>
        </tr></thead>
        <tbody>
        <?php foreach ($commands as $c): ?>
            <?php
            $payload = json_decode((string) $c['payload'], true) ?: [];
            $what = $c['task_name'] ?? (is_array($payload['task'] ?? null) ? ($payload['task']['name'] ?? null) : null);
            $version = $payload['version'] ?? null;
            ?>
            <tr>
                <td class="nowrap"><?= e(fmt_time($c['created_at'])) ?><div class="note"><?= e((string) ($c['created_by_login'] ?? '')) ?></div></td>
                <td><?= e(t('command.' . $c['kind'])) ?>
                    <?php if ($showTask && is_string($what) && $what !== ''): ?><div class="note">«<?= e($what) ?>»</div><?php endif ?>
                    <?php if (is_string($version)): ?><div class="note"><?= e($version) ?></div><?php endif ?></td>
                <td class="nowrap"><?= badge($c['status']) ?>
                    <?php if (in_array($c['status'], ['queued', 'delivered'], true)): ?>
                        <div class="note"><?= e(t('commands.expires', ['time' => fmt_time($c['expires_at'])])) ?></div>
                    <?php elseif ($c['status'] === 'cancelled' && $c['superseded_by'] !== null): ?>
                        <div class="note"><?= e(t('commands.superseded')) ?></div>
                    <?php endif ?></td>
                <td><?php if ($c['result_at'] !== null && $c['status'] !== 'cancelled'): ?><span class="note"><?= e(fmt_time($c['result_at'])) ?></span><?php endif ?>
                    <?php if ((string) $c['result_message'] !== ''): ?><div class="<?= $c['status'] === 'done' ? 'note' : 'bad-text' ?>"><?= e((string) $c['result_message']) ?></div><?php endif ?></td>
                <td><?php if ($c['status'] === 'queued'): ?>
                        <form method="post" action="<?= e(url('/agents/' . $agent['id'] . '/commands/' . $c['id'] . '/cancel')) ?>">
                            <?= csrf_field() ?><input type="hidden" name="back" value="<?= e($back) ?>">
                            <button type="submit" class="link"><?= e(t('commands.cancel')) ?></button>
                        </form>
                    <?php endif ?></td>
            </tr>
        <?php endforeach ?>
        </tbody>
    </table>
</div>
