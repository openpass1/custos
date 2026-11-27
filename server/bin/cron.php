<?php

// Background work, run every minute by the system cron (section 11):
//   * * * * * php /path/to/server/bin/cron.php
// --verbose prints what the pass did.

declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    exit("Run this script from the command line.\n");
}
require dirname(__DIR__) . '/vendor/autoload.php';

use Custos\Cli;
use Custos\Cron;
use Custos\Infra\Log;

$app = Cli::boot();
$options = Cli::options($argv);

// One pass at a time: a slow pass must not overlap with the next minute's one.
$varDir = $app->config->varDir();
if (!is_dir($varDir)) {
    @mkdir($varDir, 0700, true);
}
$lock = fopen($varDir . '/cron.lock', 'c');
if ($lock === false || !flock($lock, LOCK_EX | LOCK_NB)) {
    exit(0);
}

try {
    // A site update asked for on the site (Updates page) runs instead of the pass; so does putting back a run that
    // was cut off. The next minute's pass is done by whichever version is in place then.
    $updater = $app->updater(isset($options['verbose']) ? static function (string $key, array $params): void {
        fwrite(STDOUT, $key . ' ' . json_encode($params, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE) . "\n");
    } : null);
    $work = $updater->pending();
    if ($work !== null) {
        exit(($work === 'queued' ? $updater->runQueued($app->releases()->allowsPreview(...)) : $updater->recover()) === false ? 1 : 0);
    }

    $result = (new Cron($app))->run(time());
    if (isset($options['verbose'])) {
        fwrite(STDOUT, json_encode($result['stats'], JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE) . "\n");
    }
    foreach ($result['errors'] as $error) {
        fwrite(STDERR, $error . "\n");
    }
    exit($result['errors'] === [] ? 0 : 1);
} catch (Throwable $e) {
    Log::error("cron: $e");
    fwrite(STDERR, $e->getMessage() . "\n");
    exit(1);
} finally {
    flock($lock, LOCK_UN);
}
