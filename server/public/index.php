<?php

// Front controller: every request that is not a static file comes here.

declare(strict_types=1);

if (PHP_SAPI === 'cli-server') {
    // The built-in development server (php -S) serves files from public/ itself.
    $file = __DIR__ . (string) parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);
    if (is_file($file) && $file !== __FILE__) {
        return false;
    }
}

ini_set('display_errors', '0');
$root = dirname(__DIR__);

if (!is_file($root . '/vendor/autoload.php')) {
    http_response_code(500);
    header('Content-Type: text/plain; charset=utf-8');
    echo "CUSTOS Monitor is not installed: vendor/autoload.php is missing.\n";
    exit;
}
require $root . '/vendor/autoload.php';

try {
    $app = Custos\App::boot($root);
} catch (Throwable $e) {
    error_log('CUSTOS: ' . $e->getMessage());
    http_response_code(500);
    header('Content-Type: text/plain; charset=utf-8');
    echo "CUSTOS Monitor is not configured. See server/INSTALL.md.\n";
    exit;
}

$app->handle(Custos\Http\Request::fromGlobals($app->config->trustedProxies()))->send();
