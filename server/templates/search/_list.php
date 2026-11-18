<?php
/**
 * @var string $q @var list<string> $words lower case @var list<array> $machines @var list<array> $tasks (each with its agent)
 * @var bool $suggest the list under the search field (app.js) rather than the result page
 *
 * Matches as list items: an icon, the name with the words found in it marked, a line of facts, the state.
 */

$suggest ??= false;
// The words found are marked; the rest of the text is escaped as usual.
$mark = function (string $text) use ($words): string {
    if ($words === []) {
        return e($text);
    }
    $pattern = '/(' . implode('|', array_map(fn($w) => preg_quote($w, '/'), $words)) . ')/iu';
    $pieces = preg_split($pattern, $text, -1, PREG_SPLIT_DELIM_CAPTURE) ?: [$text];
    $html = '';
    foreach ($pieces as $i => $piece) {
        $html .= $i % 2 === 1 ? '<mark>' . e($piece) . '</mark>' : e($piece);
    }
    return $html;
};
$n = 0;
?>
<?php if ($machines !== []): ?>
    <div class="sr-head"><?= e(t('search.machines')) ?></div>
    <?php foreach ($machines as $agent): ?>
        <?php
        $facts = array_filter([$agent['hostname'] !== $agent['name'] ? $agent['hostname'] : null, $agent['last_ip'] ?? null, $agent['group_name'] ?? null]);
        $state = match (true) {
            $agent['status'] === 'pending' => 'pending',
            $agent['status'] === 'disabled' => 'disabled',
            default => $agent['online'] ? 'online' : 'offline',
        };
        ?>
        <a class="sr-item" role="option" id="sr-<?= $n++ ?>" href="<?= e(url('/agents/' . $agent['id'])) ?>">
            <span class="lead<?= $state === 'online' ? '' : ' lead-muted' ?>"><?= icon($state === 'pending' ? 'new-machine' : 'desktop') ?></span>
            <span class="sr-text"><span class="sr-title"><?= $mark($agent['name']) ?></span>
                <span class="sr-sub"><?= $facts === [] ? e(t('search.machine')) : $mark(implode(' · ', $facts)) ?></span></span>
            <?= badge($state) ?>
        </a>
    <?php endforeach ?>
<?php endif ?>
<?php if ($tasks !== []): ?>
    <div class="sr-head"><?= e(t('search.tasks')) ?></div>
    <?php foreach ($tasks as $task): ?>
        <a class="sr-item" role="option" id="sr-<?= $n++ ?>" href="<?= e(url('/agents/' . $task['agent_id'] . '/tasks/' . $task['id'])) ?>">
            <span class="lead lead-info"><?= icon('task') ?></span>
            <span class="sr-text"><span class="sr-title"><?= $mark($task['name']) ?></span>
                <span class="sr-sub"><?= $mark($task['agent']['name']) ?> · <?= e(schedule_label($task['schedule'])) ?></span></span>
            <?= badge($task['status']) ?>
        </a>
    <?php endforeach ?>
<?php endif ?>
<?php if ($machines === [] && $tasks === []): ?>
    <div class="sr-empty"><?= e(t('search.none')) ?></div>
<?php endif ?>
<?php if ($suggest && ($machines !== [] || $tasks !== [])): ?>
    <a class="sr-all" href="<?= e(url('/search', ['q' => $q])) ?>"><?= icon('search') ?><?= e(t('search.all')) ?></a>
<?php endif ?>
