<?php

// Background maintenance, run every minute by the system cron (section 11):
//   * * * * * php /path/to/server/bin/cron.php

declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    exit("Run this script from the command line.\n");
}
require dirname(__DIR__) . '/vendor/autoload.php';

use Custos\Cli;
use Custos\Infra\Log;
use Custos\Infra\Time;

$app = Cli::boot();

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
    $db = $app->db();
    // Nonces only matter for 10 minutes (A.2).
    $db->exec('DELETE FROM nonces WHERE created_at < ?', [Time::db(time() - 600)]);
    $app->settings()->set('cron_last_run', Time::now());
} catch (Throwable $e) {
    Log::error("cron: $e");
    fwrite(STDERR, $e->getMessage() . "\n");
    exit(1);
} finally {
    flock($lock, LOCK_UN);
}
