<?php

// Applies new database migrations after an update: php bin/migrate.php

declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    exit("Run this script from the command line.\n");
}
require dirname(__DIR__) . '/vendor/autoload.php';

use Custos\Cli;
use Custos\Infra\Migrator;

$app = Cli::boot();
try {
    $applied = (new Migrator($app->db(), $app->root() . '/migrations'))
        ->migrate(fn(string $file) => Cli::ok("Applied migration $file"));
} catch (Throwable $e) {
    Cli::fail($e->getMessage());
}
Cli::ok($applied === 0 ? 'Database schema is up to date' : "Applied $applied migration(s)");
foreach ($app->integrations()->ensureTokens() as $name) {
    Cli::ok("Generated $name (shown on the site under Settings)");
}
