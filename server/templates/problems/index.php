<?php
/** @var list<array> $open @var list<array> $closed @var int $page @var int $pages */
use Custos\Domain\Agents;

$time = static function (?string $utc): string {
    if ($utc === null || $utc === '') {
        return e(fmt_time($utc));
    }
    $iso = (new DateTimeImmutable($utc, new DateTimeZone('UTC')))->format(DATE_ATOM);
    return '<time datetime="' . e($iso) . '">' . e(fmt_time($utc)) . '</time>';
};
$record = static function (array $p, bool $isOpen) use ($time): string {
    ob_start(); ?>
    <li class="problem-record" data-key="p<?= (int) $p['id'] ?>">
        <div class="problem-record-main">
            <?= badge($isOpen ? $p['kind'] : 'closed', t('problem.kind.' . $p['kind'])) ?>
            <p class="problem-record-message"><?= e(problem_text($p)) ?></p>
        </div>
        <dl class="problem-record-facts">
            <div>
                <dt><?= e(t('problems.col.agent')) ?></dt>
                <dd><a href="<?= e(url('/agents/' . $p['agent_id'])) ?>"><?= e(Agents::name($p)) ?></a></dd>
            </div>
            <div>
                <dt><?= e(t('problems.col.task')) ?></dt>
                <dd><a href="<?= e(url('/agents/' . $p['agent_id'] . '/tasks/' . $p['task_id'])) ?>"><?= e($p['task_name'] ?? t('runs.unknown_task')) ?></a></dd>
            </div>
        </dl>
        <dl class="problem-record-facts problem-record-timing">
            <div>
                <dt><?= e(t('problems.col.opened')) ?></dt>
                <dd>
                    <?= $time($p['opened_at']) ?>
                    <?php if ($isOpen): ?><span class="problem-record-detail"><?= e(fmt_ago($p['opened_at'])) ?></span><?php endif ?>
                </dd>
            </div>
            <?php if (!$isOpen): ?>
                <div>
                    <dt><?= e(t('problems.col.closed')) ?></dt>
                    <dd><?= $time($p['closed_at']) ?><span class="problem-record-detail"><?= e(t('closed_by.' . $p['closed_by'])) ?></span></dd>
                </div>
            <?php endif ?>
            <div>
                <dt><?= e(t('problems.col.notify')) ?></dt>
                <dd><?= e(t('notify.state.' . $p['notify'])) ?></dd>
            </div>
        </dl>
    </li>
    <?php return (string) ob_get_clean();
};
?>
<h1><?= e(t('problems.title')) ?></h1>
<p class="hint problems-intro"><?= e(t('problems.hint')) ?></p>

<?php /* Live parts: problems that open or close show up by themselves (app.js). */ ?>
<div data-part="open">
<section class="panel<?= $open === [] ? ' panel-empty' : '' ?>" aria-labelledby="problems-open">
    <h2 id="problems-open"><?= e(t('problems.open.title')) ?></h2>
    <?php if ($open === []): ?>
        <p class="all-good"><?= e(t('dashboard.no_problems')) ?></p>
    <?php else: ?>
        <ul class="problem-records" role="list">
            <?php foreach ($open as $p): ?><?= $record($p, true) ?><?php endforeach ?>
        </ul>
    <?php endif ?>
</section>

</div>

<div data-part="closed">
<section class="panel<?= $closed === [] ? ' panel-empty' : '' ?>" aria-labelledby="problems-closed">
    <h2 id="problems-closed"><?= e(t('problems.closed.title')) ?></h2>
    <?php if ($closed === []): ?>
        <p class="empty"><?= e(t('problems.closed.none')) ?></p>
    <?php else: ?>
        <ul class="problem-records" role="list">
            <?php foreach ($closed as $p): ?><?= $record($p, false) ?><?php endforeach ?>
        </ul>
        <?= pager($page, $pages, '/problems', []) ?>
    <?php endif ?>
</section>
</div>
