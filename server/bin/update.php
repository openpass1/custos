<?php

// Updates the site (the server folder) to a newer version, run as the site user from the server folder:
//
//   php bin/update.php                    the newest release from GitHub: shows the versions and asks
//   php bin/update.php --check            only shows the installed and the newest version
//   php bin/update.php --package=FILE     from a downloaded custos-server-X.Y.Z.zip (a server without internet);
//                                         a pattern in quotes such as '../custos-server*.zip' takes the newest file
//   php bin/update.php --rollback         returns to the version before the last update
//   --yes                                 asks nothing
//   --force                               with --package: installs the same version again (a repair)
//
// It does what the «Update» button of the site does (Updates page): checks the package, puts up the "being updated"
// page, keeps the old files in var/update/backup, runs the migrations and checks the new version, and puts the old
// version back by itself when something fails. Settings (config/), keys and var/ are not touched.
//
// The updater also runs "php bin/update.php --self-test --expect=X.Y.Z" of the new version: it checks that this
// installation works (syntax of every PHP file, classes, database, a page) and exits with 0 when it does.

declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    exit("Run this script from the command line.\n");
}
require dirname(__DIR__) . '/vendor/autoload.php';

use Custos\App;
use Custos\Cli;
use Custos\Http\Request;
use Custos\Infra\Lang;
use Custos\Infra\Migrator;
use Custos\Infra\UpdateError;
use Custos\Infra\Updater;

$options = Cli::options($argv);
$app = Cli::boot();

if (isset($options['self-test'])) {
    exit(selfTest($app, is_string($options['expect'] ?? null) ? $options['expect'] : null));
}

// Messages in English, like the other scripts; loaded now, before the files may be replaced.
$lang = new Lang($app->root() . '/lang', 'en');
$say = static function (string $key, array $params) use ($lang): void {
    Cli::info($lang->get($key, $params));
};
$updater = $app->updater($say);
$by = 'command line (' . Updater::currentUser() . ')';
$yes = isset($options['yes']);

// One run at a time, and never together with a cron pass (cron takes the same lock).
$varDir = $app->config->varDir();
if (!is_dir($varDir)) {
    @mkdir($varDir, 0700, true);
}
$lock = fopen($varDir . '/cron.lock', 'c');
if ($lock === false) {
    Cli::fail("Cannot open $varDir/cron.lock");
}
for ($i = 0; !flock($lock, LOCK_EX | LOCK_NB); $i++) {
    if ($i === 0) {
        Cli::info('Waiting for the running cron pass to finish...');
    }
    if ($i >= 120) {
        Cli::fail('bin/cron.php has been running for two minutes: try again later.');
    }
    sleep(1);
}

$message = static fn(UpdateError $e): string => $lang->get($e->key, $e->params);

// A run that was cut off is put right first.
if ($updater->pending() === 'interrupted') {
    Cli::warn('The previous update was interrupted: putting the old version back.');
    $updater->recover();
}

if (isset($options['rollback'])) {
    $backup = $updater->backupInfo();
    if ($backup === null) {
        Cli::fail($lang->get('site.error.no_backup'));
    }
    Cli::info('Installed: ' . $updater->installedVersion() . ', the copy kept by the last update: ' . $backup['version']
        . ' (' . $backup['created_at'] . ' UTC).');
    if (!empty($backup['dump'])) {
        $restored = array_values(array_diff($backup['dump']['tables'], ['schema_migrations']));
        $created = array_values(array_diff($app->db()->column('SHOW TABLES'), $backup['dump']['tables_before']));
        if ($restored !== []) {
            Cli::warn('That update changed the database. These tables go back to their state at ' . $backup['created_at']
                . ' UTC, and what was written to them since then is lost: ' . implode(', ', $restored) . '.');
        }
        if ($created !== []) {
            Cli::warn('These tables of the newer version are deleted: ' . implode(', ', $created) . '.');
        }
    }
    if (!$yes && strtolower(Cli::prompt("Return to {$backup['version']}? [y/N] ")) !== 'y') {
        Cli::fail('Nothing changed.');
    }
    try {
        $updater->rollback($by);
    } catch (UpdateError $e) {
        Cli::fail($message($e));
    }
    Cli::ok("Version {$backup['version']} is back. The version is shown at the bottom of every page of the site.");
    exit(0);
}

$installed = $updater->installedVersion();
if (isset($options['package'])) {
    $file = (string) $options['package'];
    if (!is_file($file) && str_contains($file, '*')) {
        // --package='../custos-server*.zip': the guide does not need to know the version; the newest file wins.
        $files = glob($file) ?: [];
        usort($files, fn(string $a, string $b) => version_compare(
            (string) preg_replace('/^.*?(\d+(?:\.\d+)*)\.zip$/', '$1', $b), (string) preg_replace('/^.*?(\d+(?:\.\d+)*)\.zip$/', '$1', $a)));
        $file = $files[0] ?? $file;
    }
    try {
        $info = Updater::inspect($file);
    } catch (UpdateError $e) {
        Cli::fail($message($e));
    }
    Cli::info("Installed: $installed, package: {$info['version']}");
    $force = isset($options['force']);
    if (version_compare($info['version'], $installed, $force ? '<' : '<=')) {
        Cli::ok(version_compare($info['version'], $installed, '=')
            ? "Version $installed is already installed. Nothing to do (--force installs it again)."
            : "The package is older than the installed version $installed. Nothing to do.");
        exit(0);
    }
    if (!$yes && strtolower(Cli::prompt("Update the site to {$info['version']}? [y/N] ")) !== 'y') {
        Cli::fail('Nothing changed.');
    }
    $ok = $updater->installFile((string) realpath($file), $by, $force);
} else {
    $releases = $app->releases();
    Cli::info("Reading the releases of github.com/{$releases->repo} ({$releases->channel()} channel)");
    if (!$releases->check(time())) {
        Cli::fail('Cannot read the releases: ' . $app->settings()->get('releases_check_error')
            . '. Download custos-server-X.Y.Z.zip from the Releases page by hand and run: php bin/update.php --package=FILE');
    }
    $latest = $releases->latest('server');
    if ($latest === null) {
        Cli::warn('No site release found in the selected channel. Nothing to do.');
        exit(0);
    }
    Cli::info("Installed: $installed, newest release: {$latest['version']}" . (!empty($latest['prerelease']) ? ' (pre-release)' : '')
        . ($latest['url'] !== '' ? " ({$latest['url']})" : ''));
    if (version_compare($latest['version'], $installed, '<=')) {
        Cli::ok('No newer site version in the selected channel.');
        exit(0);
    }
    if (isset($options['check'])) {
        exit(0);
    }
    if (!$yes && strtolower(Cli::prompt("Update the site to {$latest['version']}? [y/N] ")) !== 'y') {
        Cli::fail('Nothing changed.');
    }
    $ok = $updater->installRelease($latest, $by);
}

$state = Updater::readState($varDir) ?? [];
if ($ok) {
    Cli::ok("The site is updated: {$state['from']} -> {$state['to']}. The version is shown at the bottom of every page of the site.");
    Cli::info('To return to the previous version: php bin/update.php --rollback');
    exit(0);
}
$error = (array) ($state['error'] ?? []);
fwrite(STDERR, '[error] ' . $lang->get((string) ($error['key'] ?? 'site.error.unexpected'), (array) ($error['params'] ?? [])) . "\n");
if (($state['output'] ?? '') !== '') {
    fwrite(STDERR, "Output of the new version:\n" . $state['output'] . "\n");
}
if (isset($state['rollback_error'])) {
    fwrite(STDERR, '[error] ' . $lang->get($state['rollback_error']['key'], $state['rollback_error']['params']) . "\n");
} elseif (!empty($state['rolled_back'])) {
    fwrite(STDERR, "The site is back on version {$state['from']}; nothing else changed.\n");
} else {
    fwrite(STDERR, "Nothing changed.\n");
}
exit(1);

/**
 * Checks this installation after an update: the version, the syntax of every PHP file of the site, every class,
 * the database schema and one page. Returns the exit code.
 */
function selfTest(App $app, ?string $expect): int
{
    $problems = [];
    if ($expect !== null && $expect !== App::VERSION) {
        $problems[] = 'version ' . App::VERSION . ' instead of ' . $expect;
    }
    $root = $app->root();
    $checked = 0;
    foreach (['src', 'templates', 'lang', 'bin', 'public', 'tools'] as $dir) {
        if (!is_dir("$root/$dir")) {
            continue;
        }
        foreach (new RecursiveIteratorIterator(new RecursiveDirectoryIterator("$root/$dir", FilesystemIterator::SKIP_DOTS)) as $file) {
            if ($file->getExtension() !== 'php') {
                continue;
            }
            try {
                PhpToken::tokenize((string) file_get_contents($file->getPathname()), TOKEN_PARSE);
                $checked++;
            } catch (ParseError $e) {
                $problems[] = $file->getPathname() . ':' . $e->getLine() . ': ' . $e->getMessage();
            }
        }
    }
    if ($problems === []) {
        foreach (new RecursiveIteratorIterator(new RecursiveDirectoryIterator("$root/src", FilesystemIterator::SKIP_DOTS)) as $file) {
            $relative = substr($file->getPathname(), strlen("$root/src/"), -4);
            if ($file->getExtension() !== 'php' || $relative === 'helpers') {
                continue;
            }
            $class = 'Custos\\' . str_replace('/', '\\', $relative);
            if (!class_exists($class) && !interface_exists($class) && !trait_exists($class) && !enum_exists($class)) {
                $problems[] = "class $class is not found in " . $file->getPathname();
            }
        }
    }
    try {
        $pending = (new Migrator($app->db(), $root . '/migrations'))->pending();
        if ($pending !== []) {
            $problems[] = 'migrations not applied: ' . implode(', ', $pending);
        }
        $response = $app->handle(new Request('GET', '/login', ['user-agent' => 'CUSTOS self-test']));
        if ($response->status >= 500) {
            $problems[] = "the login page answers {$response->status}";
        }
    } catch (Throwable $e) {
        $problems[] = $e->getMessage();
    }
    if ($problems !== []) {
        fwrite(STDERR, "[error] Self-test of version " . App::VERSION . " failed:\n  " . implode("\n  ", $problems) . "\n");
        return 1;
    }
    Cli::ok('Self-test of version ' . App::VERSION . " passed ($checked PHP files)");
    return 0;
}
