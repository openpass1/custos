<?php
/** @var list<array> $chart points of Analytics::taskChart, oldest first */
$zone = new DateTimeZone(\Custos\Http\Web\View::$current->app->config->timezone());
$data = [
    'labels' => array_map(fn($p) => fmt_time($p['at']), $chart),
    'days' => array_map(fn($p) => (new DateTimeImmutable($p['at'], new DateTimeZone('UTC')))->setTimezone($zone)->format(t('format.day')), $chart),
    'runs' => array_map(fn($p) => url('/runs/' . $p['id']), $chart),
    'status' => array_map(fn($p) => $p['status'], $chart),
    'anomaly' => array_map(fn($p) => $p['anomaly'], $chart),
    'source' => array_map(fn($p) => $p['source_bytes'], $chart),
    'files' => array_map(fn($p) => $p['source_files'], $chart),
    'written' => array_map(fn($p) => $p['written'], $chart),
    'duration' => array_map(fn($p) => $p['duration'], $chart),
    'text' => [
        'source' => t('chart.source'), 'files' => t('chart.files'), 'written' => t('chart.written'), 'duration' => t('chart.duration'),
        'anomaly' => t('chart.anomaly'), 'minutes' => t('chart.minutes'),
        'units' => [t('unit.b'), t('unit.kb'), t('unit.mb'), t('unit.gb'), t('unit.tb')], 'decimal' => t('format.decimal'),
    ],
];
?>
<section class="panel">
    <h2><?= e(t('chart.title', ['n' => count($chart), 'days' => \Custos\Domain\Analytics::CHART_DAYS])) ?></h2>
    <?php if (count($chart) < 2): ?>
        <p class="empty"><?= e(t('chart.too_few')) ?></p>
    <?php else: ?>
        <p class="hint"><?= e(t('chart.hint')) ?></p>
        <div class="charts">
            <figure><figcaption><?= e(t('chart.source')) ?></figcaption><div class="chart-box"><canvas data-chart="source" aria-label="<?= e(t('chart.source')) ?>" role="img"></canvas></div></figure>
            <figure><figcaption><?= e(t('chart.written')) ?></figcaption><div class="chart-box"><canvas data-chart="written" aria-label="<?= e(t('chart.written')) ?>" role="img"></canvas></div></figure>
            <figure><figcaption><?= e(t('chart.duration')) ?></figcaption><div class="chart-box"><canvas data-chart="duration" aria-label="<?= e(t('chart.duration')) ?>" role="img"></canvas></div></figure>
        </div>
        <script type="application/json" id="task-chart-data"><?= json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_HEX_TAG | JSON_HEX_AMP) ?></script>
    <?php endif ?>
</section>
