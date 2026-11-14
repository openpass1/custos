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

// A site update (bin/update.php, or cron after «Update» on the site) works in var/update. Checked before any class
// is loaded, because the classes are being replaced. Nothing here may change its meaning in later versions: the
// version that runs this file during an update is the old one.
$settings = getenv('CUSTOS_CONFIG') ?: $root . '/config/config.php';
$settings = is_file($settings) ? (require $settings) : [];
$updateDir = (is_array($settings) && isset($settings['var_dir']) ? (string) $settings['var_dir'] : $root . '/var') . '/update';
unset($settings);
// After an update: PHP may keep the old compiled files (opcache.validate_timestamps=0); the next request clears them.
if (is_file($updateDir . '/opcache-reset') && @unlink($updateDir . '/opcache-reset') && function_exists('opcache_reset')) {
    opcache_reset();
}
if (is_file($updateDir . '/maintenance.json')) {
    $update = json_decode((string) @file_get_contents($updateDir . '/maintenance.json'), true);
    $version = is_array($update) && is_string($update['version'] ?? null) ? preg_replace('/[^0-9A-Za-z.+-]/', '', $update['version']) : '';
    http_response_code(503);
    header('Retry-After: 30');
    header('Cache-Control: no-store');
    header('X-Custos-Maintenance: 1');
    $path = (string) parse_url((string) ($_SERVER['REQUEST_URI'] ?? '/'), PHP_URL_PATH);
    if (str_contains($path, '/api/')) {
        // Agents (unsigned 5xx: they try again in a minute, A.11), Zabbix and the wall screen.
        header('Content-Type: application/json');
        echo json_encode(['error' => 'maintenance', 'message' => 'The site is being updated' . ($version !== '' ? " to $version" : '') . '; try again in a minute.']);
        exit;
    }
    header('Content-Type: text/html; charset=utf-8');
    [$toRu, $toEn] = $version !== '' ? [" до версии $version", " to version $version"] : ['', ''];
    echo <<<HTML
        <!doctype html>
        <html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="refresh" content="5"><title>CUSTOS Monitor</title>
        <style>
        body { font: 16px/1.5 system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center;
               background: #f6f7f9; color: #1d2430; }
        main { max-width: 34rem; padding: 2rem 1.5rem; }
        h1 { font-size: 1.4rem; margin: 0 0 .5rem; }
        p { margin: .25rem 0 1rem; color: #4a5465; }
        @media (prefers-color-scheme: dark) { body { background: #12161d; color: #e6e9ee; } p { color: #a9b1bf; } }
        </style></head>
        <body><main>
        <h1>Сайт обновляется{$toRu}</h1>
        <p>Обычно это занимает меньше минуты. Страница откроется сама. Машины подождут и пришлют данные позже.</p>
        <h1 lang="en">The site is being updated{$toEn}</h1>
        <p lang="en">This usually takes less than a minute. The page will open by itself. Machines wait and send their data later.</p>
        </main></body></html>
        HTML;
    exit;
}

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
