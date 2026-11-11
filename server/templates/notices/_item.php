<?php
/** @var array $n a notice with hostname/display_name of its agent */
$link = null;
if ($n['agent_id'] !== null && ($n['hostname'] ?? null) !== null) {
    $link = $n['task_id'] !== null && !str_starts_with($n['kind'], 'agent.')
        ? url('/agents/' . $n['agent_id'] . '/tasks/' . $n['task_id'])
        : url('/agents/' . $n['agent_id']);
}
?>
<li class="notice<?= $n['read_at'] === null ? ' unread' : '' ?>">
    <span class="note"><?= e(fmt_time($n['created_at'])) ?></span>
    <?php if ($link !== null): ?><a href="<?= e($link) ?>"><?= e(notice_text($n)) ?></a><?php else: ?><?= e(notice_text($n)) ?><?php endif ?>
</li>
