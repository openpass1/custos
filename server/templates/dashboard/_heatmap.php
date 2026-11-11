<?php
/** @var array{days: list<string>, total: array<string, array>, rows: list<array{agent: array, cells: array<string, array>}>} $heatmap */
$cell = function (array $c, string $day, ?string $agentId) {
    $title = fmt_day($day);
    foreach (\Custos\Domain\Analytics::DAY_ORDER as $result) {
        if (($c['counts'][$result] ?? 0) > 0) {
            $title .= ' · ' . t('heatmap.' . $result) . ': ' . (int) $c['counts'][$result];
        }
    }
    $class = 'heat heat-' . ($c['worst'] ?? 'none');
    if ($c['worst'] === null) {
        return '<span class="' . $class . '" title="' . e($title . ' · ' . t('heatmap.none')) . '"></span>';
    }
    $href = url('/runs', ['agent' => $agentId, 'from' => $day, 'to' => $day]);
    return '<a class="' . $class . '" href="' . e($href) . '" title="' . e($title) . '"><span class="visually-hidden">' . e($title) . '</span></a>';
};
?>
<section class="panel">
    <h2><?= e(t('heatmap.title', ['days' => count($heatmap['days'])])) ?></h2>
    <?php if ($heatmap['rows'] === []): ?>
        <p class="empty"><?= e(t('heatmap.empty')) ?></p>
    <?php else: ?>
        <div class="table-wrap">
            <table class="heatmap">
                <thead>
                <tr>
                    <th></th>
                    <?php foreach ($heatmap['days'] as $i => $day): ?>
                        <?php $weekday = (int) (new DateTimeImmutable($day))->format('N'); ?>
                        <th class="heat-day<?= $weekday >= 6 ? ' weekend' : '' ?>" title="<?= e(fmt_day($day)) ?>"><?= e($i % 2 === (count($heatmap['days']) - 1) % 2 ? (int) substr($day, 8, 2) : '') ?></th>
                    <?php endforeach ?>
                </tr>
                </thead>
                <tbody>
                <tr class="heat-total">
                    <th><?= e(t('heatmap.all')) ?></th>
                    <?php foreach ($heatmap['days'] as $day): ?><td><?= $cell($heatmap['total'][$day], $day, null) ?></td><?php endforeach ?>
                </tr>
                <?php foreach ($heatmap['rows'] as $row): ?>
                    <tr>
                        <th class="ellipsis"><a href="<?= e(url('/agents/' . $row['agent']['id'])) ?>" title="<?= e($row['agent']['name']) ?>"><?= e($row['agent']['name']) ?></a></th>
                        <?php foreach ($heatmap['days'] as $day): ?><td><?= $cell($row['cells'][$day], $day, $row['agent']['id']) ?></td><?php endforeach ?>
                    </tr>
                <?php endforeach ?>
                </tbody>
            </table>
        </div>
        <div class="legend heat-legend">
            <?php foreach (['success', 'warning', 'missed_offline', 'missed', 'error'] as $result): ?>
                <span><span class="heat heat-<?= e($result) ?>"></span> <?= e(t('heatmap.' . $result)) ?></span>
            <?php endforeach ?>
            <span><span class="heat heat-none"></span> <?= e(t('heatmap.none')) ?></span>
        </div>
    <?php endif ?>
</section>
