<?php

// Installs CUSTOS Monitor or completes an existing installation. Safe to run again: existing keys, users
// and the registration key are kept.
//
//   php bin/install.php                       asks for the first administrator's login and password
//   php bin/install.php --admin-login=admin   takes the password from the CUSTOS_ADMIN_PASSWORD environment variable
//   php bin/install.php --language=en         sets the default language of the site (ru or en; Settings change it later)
//   php bin/install.php --cron-added          leaves out the cron hint (install.sh writes the cron job itself)

declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    exit("Run this script from the command line.\n");
}
if (!is_file(dirname(__DIR__) . '/vendor/autoload.php')) {
    fwrite(STDERR, "[error] vendor/autoload.php is missing. Run \"composer install --no-dev\" in the server folder first.\n");
    exit(1);
}
require dirname(__DIR__) . '/vendor/autoload.php';

use Custos\Cli;
use Custos\Domain\RegistrationKey;
use Custos\Domain\Users;
use Custos\Infra\Migrator;

$options = Cli::options($argv);
$language = $options['language'] ?? null;
if ($language !== null && !in_array($language, Custos\App::LANGUAGES, true)) {
    Cli::fail('--language must be one of: ' . implode(', ', Custos\App::LANGUAGES));
}

// 1. PHP version and extensions (section 2).
if (PHP_VERSION_ID < 80300) {
    Cli::fail('PHP 8.3 or newer is required, this is ' . PHP_VERSION . '. In CloudPanel run the script with the PHP version of the site, for example php8.4.');
}
$missing = array_filter(['pdo_mysql', 'openssl', 'curl', 'mbstring', 'json', 'zlib'], fn($ext) => !extension_loaded($ext));
if ($missing !== []) {
    Cli::fail('Missing PHP extensions: ' . implode(', ', $missing));
}
Cli::ok('PHP ' . PHP_VERSION . ' with all required extensions');

// 2. Configuration and database.
$app = Cli::boot();
try {
    $db = $app->db();
} catch (PDOException $e) {
    Cli::fail('Cannot connect to the database: ' . $e->getMessage() . '. Check the "db" section of config/config.php.');
}
$version = (string) $db->value('SELECT VERSION()');
$isMaria = stripos($version, 'mariadb') !== false;
if (version_compare(preg_replace('/^(\d+\.\d+\.\d+).*/', '$1', $version), $isMaria ? '10.11.0' : '8.0.0', '<')) {
    Cli::warn("Database $version is older than MariaDB 10.11 / MySQL 8.0; this is not supported.");
} else {
    Cli::ok("Database $version");
}

// 3. Migrations.
$applied = (new Migrator($db, $app->root() . '/migrations'))->migrate(fn(string $file) => Cli::ok("Applied migration $file"));
if ($applied === 0) {
    Cli::ok('Database schema is up to date');
}

// 4. Writable folders.
foreach (['', '/sessions', '/log'] as $sub) {
    $dir = $app->config->varDir() . $sub;
    if (!is_dir($dir) && !mkdir($dir, 0700, true) && !is_dir($dir)) {
        Cli::fail("Cannot create $dir");
    }
}
Cli::ok('Folder ' . $app->config->varDir() . ' is ready');

// 5. Server keys (A.2).
$keys = $app->keys();
try {
    $created = $keys->create();
    $keys->privateKey();
} catch (RuntimeException $e) {
    Cli::fail($e->getMessage() . ' If the files belong to root, change their owner to the site user (chown -R) and run this script again as the site user.');
}
Cli::ok($created ? 'Created the server key pair in ' . $app->config->keysDir() : 'Server key pair already exists');

// 6. The first administrator.
$users = $app->users();
if ($users->count() === 0) {
    $login = is_string($options['admin-login'] ?? null) ? $options['admin-login'] : Cli::prompt('Login of the first administrator: ');
    $password = getenv('CUSTOS_ADMIN_PASSWORD') ?: '';
    if ($password === '') {
        $password = Cli::promptHidden('Password (at least ' . Users::MIN_PASSWORD . ' characters): ');
        if ($password !== Cli::promptHidden('Repeat the password: ')) {
            Cli::fail('Passwords do not match. Run the script again.');
        }
    }
    try {
        $users->create($login, $password);
    } catch (InvalidArgumentException $e) {
        Cli::fail(match ($e->getMessage()) {
            'users.error.login' => 'Login must be 1-64 letters, digits or . _ @ -',
            'users.error.password' => 'Password must be at least ' . Users::MIN_PASSWORD . ' characters',
            default => $e->getMessage(),
        });
    }
    Cli::ok("Created administrator \"$login\"");
} else {
    Cli::ok('Users already exist, none created');
}

// 7. Agent registration key (A.3).
$settings = $app->settings();
if ($settings->get('registration_key') === '') {
    $settings->set('registration_key', RegistrationKey::generate());
    Cli::ok('Generated the agent registration key');
}

// 8. Tokens of Zabbix and the wall screen (sections 7.9, 10).
foreach ($app->integrations()->ensureTokens() as $name) {
    Cli::ok("Generated $name");
}

// 9. Default language of the site and of the messages (--language).
if (is_string($language)) {
    $settings->set('default_language', $language);
    Cli::ok("Default language: $language");
}

Cli::info('');
Cli::info('Server key fingerprint:  ' . $keys->fingerprint());
// What a machine needs to connect, in one line: the site address and the registration code (A.3).
$code = RegistrationKey::code($settings->get('registration_key'), $keys->fingerprint());
if ($app->config->url() === '') {
    Cli::info('Registration code:       ' . $code);
    Cli::info("Set 'url' in config/config.php: the site then shows the connection string for the machines under Settings.");
} else {
    Cli::info('Connection string:       ' . RegistrationKey::connection($app->config->url(), $code));
    Cli::info('It is also shown on the site under Settings.');
}
if (!isset($options['cron-added'])) {
    Cli::info('');
    Cli::info('Add a cron job that runs every minute:');
    Cli::info('  * * * * * ' . PHP_BINARY . ' ' . $app->root() . '/bin/cron.php');
}
