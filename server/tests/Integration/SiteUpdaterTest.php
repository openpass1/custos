<?php

declare(strict_types=1);

namespace Custos\Tests\Integration;

use Custos\App;
use Custos\Infra\Config;
use Custos\Infra\Db;
use Custos\Infra\Updater;
use Custos\Tests\TestEnv;
use Custos\Tests\ZipWriter;
use PHPUnit\Framework\TestCase;

/**
 * The site updating itself (Infra\Updater) on a copy of the site in a temporary folder: the code of this repository
 * with a small vendor/autoload.php, so the new version's bin/migrate.php and bin/update.php --self-test really run in
 * their own processes. Packages are this copy with another version number and extra migrations or files.
 * tests/update/run.sh does the same with the real package that CI builds.
 */
final class SiteUpdaterTest extends TestCase
{
    private App $app;
    private string $site;
    private string $root;
    private string $var;
    private string $config;
    private array $said = [];

    protected function setUp(): void
    {
        // The test database with the schema of this version, the server keys and the settings of an installed site.
        $this->app = TestEnv::app($this);
        $this->site = TestEnv::dir() . '/site-' . bin2hex(random_bytes(3));
        $this->root = $this->site . '/server';
        $this->var = $this->root . '/var';
        $repo = dirname(__DIR__, 2);
        foreach (['bin', 'lang', 'migrations', 'public', 'src', 'templates', 'tools'] as $dir) {
            self::copyTree("$repo/$dir", "$this->root/$dir");
        }
        copy("$repo/composer.json", "$this->root/composer.json");
        mkdir("$this->root/config");
        copy("$repo/config/config.example.php", "$this->root/config/config.example.php");
        mkdir("$this->root/vendor");
        file_put_contents("$this->root/vendor/autoload.php", self::autoloader("$repo/vendor"));
        $this->config = "$this->root/config/config.php";
        file_put_contents($this->config, '<?php return ' . var_export(['var_dir' => $this->var] + TestEnv::config(), true) . ';');
        // Scripts started by the updater (and by this test) use this copy's settings.
        putenv('CUSTOS_CONFIG=' . $this->config);
    }

    protected function tearDown(): void
    {
        putenv('CUSTOS_CONFIG');
        @chmod("$this->root/src/Domain", 0755);
    }

    public function testUpdateKeepsTheOldVersionAndRollsBack(): void
    {
        $db = $this->app->db();
        $this->app->settings()->set('server_name', 'Before the update');
        mkdir("$this->root/public/.well-known/acme-challenge", 0755, true);
        file_put_contents("$this->root/public/.well-known/acme-challenge/token", 'certificate check');
        $package = $this->package('0.9.1', [
            'migrations/900_update_test.sql' => "CREATE TABLE update_test (id INT NOT NULL, PRIMARY KEY (id)) ENGINE=InnoDB;\n"
                . "INSERT INTO update_test (id) VALUES (1);\nALTER TABLE settings ADD COLUMN update_test INT NULL;\n"
                . "UPDATE settings SET value = 'After the update' WHERE name = 'server_name';\n",
            'NEW-IN-0.9.1.txt' => "a file the old version does not have\n",
        ]);

        $this->assertTrue($this->updater()->installFile($package, 'test'), $this->explain());
        $this->assertSame('0.9.1', $this->version());
        $this->assertFileExists("$this->root/NEW-IN-0.9.1.txt");
        $this->assertSame('certificate check', file_get_contents("$this->root/public/.well-known/acme-challenge/token"),
            'files of public/ that are not ours stay');
        $this->assertSame(1, (int) $db->value('SELECT COUNT(*) FROM update_test'));
        $this->assertSame('After the update', $db->value("SELECT value FROM settings WHERE name = 'server_name'"));
        $state = Updater::readState($this->var);
        $this->assertSame('done', $state['status']);
        $this->assertSame([App::VERSION, '0.9.1'], [$state['from'], $state['to']]);
        $this->assertSame(['checks', 'unpack', 'maintenance', 'backup', 'files', 'database', 'selftest'], array_column($state['steps'], 'step'));
        $this->assertStringContainsString('Applied migration 900_update_test.sql', $state['output']);
        $this->assertStringContainsString('Self-test of version 0.9.1 passed', $state['output']);
        $this->assertFileDoesNotExist(Updater::maintenanceFile($this->var));
        $this->assertFileExists(Updater::opcacheFile($this->var), 'index.php is told to drop the compiled old files');
        $this->assertDirectoryDoesNotExist("$this->var/update/stage");
        $backup = $this->updater()->backupInfo();
        $this->assertSame([App::VERSION, '0.9.1'], [$backup['version'], $backup['to']]);
        $this->assertEqualsCanonicalizing(['settings', 'schema_migrations'], $backup['dump']['tables'],
            'the tables the migration changes (update_test does not exist yet) and the list of migrations');
        $this->assertNotContains('update_test', $backup['dump']['tables_before']);
        $this->assertSame(1, (int) $db->value("SELECT COUNT(*) FROM notices WHERE kind = 'site.updated'"));
        $this->assertContains('site.step.done', array_column($this->said, 0));

        // Back to the version before: files, the settings example, the tables; the new table goes away.
        $this->assertTrue($this->updater()->rollback('test'), $this->explain());
        $this->assertSame(App::VERSION, $this->version());
        $this->assertFileDoesNotExist("$this->root/NEW-IN-0.9.1.txt");
        $this->assertSame([], $db->column("SHOW TABLES LIKE 'update_test'"));
        $this->assertSame([], $db->column("SHOW COLUMNS FROM settings LIKE 'update_test'"));
        $this->assertSame('Before the update', $db->value("SELECT value FROM settings WHERE name = 'server_name'"));
        $this->assertSame(0, (int) $db->value("SELECT COUNT(*) FROM schema_migrations WHERE version = '900_update_test.sql'"));
        $this->assertSame('rolled_back', Updater::readState($this->var)['status']);
        $this->assertNull($this->updater()->backupInfo(), 'the copy is used up');
        $this->assertSame('certificate check', file_get_contents("$this->root/public/.well-known/acme-challenge/token"));
        // The old version works: its own self-test passes.
        [$code, $output] = $this->php(['bin/update.php', '--self-test', '--expect=' . App::VERSION]);
        $this->assertSame(0, $code, $output);
    }

    public function testFailedMigrationPutsFilesAndTablesBack(): void
    {
        $db = $this->app->db();
        $package = $this->package('0.9.2', [
            'migrations/900_broken.sql' => "ALTER TABLE settings ADD COLUMN update_broken INT NULL;\nTHIS IS NOT SQL;\n",
        ]);

        $this->assertFalse($this->updater()->installFile($package, 'test'));
        $state = Updater::readState($this->var);
        $this->assertSame('failed', $state['status']);
        $this->assertSame('site.error.migrate', $state['error']['key']);
        $this->assertTrue($state['rolled_back']);
        $this->assertStringContainsString('syntax', $state['output'], 'what migrate.php of the new version printed');
        $this->assertSame('rollback', end($state['steps'])['step']);
        $this->assertSame(App::VERSION, $this->version());
        $this->assertSame([], $db->column("SHOW COLUMNS FROM settings LIKE 'update_broken'"), 'the half-applied migration is undone');
        $this->assertSame(0, (int) $db->value("SELECT COUNT(*) FROM schema_migrations WHERE version = '900_broken.sql'"));
        $this->assertFileDoesNotExist(Updater::maintenanceFile($this->var));
        $this->assertDirectoryDoesNotExist("$this->var/update/backup.new");
        $this->assertDirectoryDoesNotExist("$this->var/update/stage");
        $this->assertSame(1, (int) $db->value("SELECT COUNT(*) FROM notices WHERE kind = 'site.update_failed'"));
        [$code, $output] = $this->php(['bin/update.php', '--self-test', '--expect=' . App::VERSION]);
        $this->assertSame(0, $code, $output);
    }

    public function testNewVersionThatDoesNotStartIsTakenBack(): void
    {
        $package = $this->package('0.9.3', ['templates/broken.php' => "<?php if (true) { ?>\n<p>never closed\n"]);

        $this->assertFalse($this->updater()->installFile($package, 'test'));
        $state = Updater::readState($this->var);
        $this->assertSame('site.error.selftest', $state['error']['key']);
        $this->assertStringContainsString('templates/broken.php', $state['output']);
        $this->assertSame(App::VERSION, $this->version());
        $this->assertFileDoesNotExist("$this->root/templates/broken.php");
    }

    public function testQueuedOnTheSiteAndRunByCron(): void
    {
        $site = new App(Config::fromArray(require $this->config, $this->root));
        $package = $this->package('0.9.4');
        $state = $site->siteUpdate()->upload($package, 'admin');
        $this->assertSame(['queued', 'file', '0.9.4'], [$state['status'], $state['source'], $state['to']]);
        try {
            $site->siteUpdate()->upload($package, 'admin', $params);
            $this->fail('one update at a time');
        } catch (\InvalidArgumentException $e) {
            $this->assertSame('site.error.busy', $e->getMessage());
        }

        // cron.php of the site finds it and runs it instead of its pass.
        [$code, $output] = $this->php(['bin/cron.php', '--verbose']);
        $this->assertSame(0, $code, $output);
        $this->assertStringContainsString('site.step.selftest', $output);
        $this->assertSame('0.9.4', $this->version());
        $state = Updater::readState($this->var);
        $this->assertSame(['done', 'admin'], [$state['status'], $state['by']]);
        $this->assertDirectoryDoesNotExist("$this->var/update/upload", 'the uploaded file is deleted');

        // The next minute is an ordinary pass of the new version.
        [$code, $output] = $this->php(['bin/cron.php', '--verbose']);
        $this->assertSame(0, $code, $output);
        $this->assertStringContainsString('"monitor"', $output);
    }

    public function testInterruptedRunIsPutBack(): void
    {
        $db = $this->app->db();
        $package = $this->package('0.9.5', ['migrations/900_interrupted.sql' => "CREATE TABLE update_interrupted (id INT NOT NULL) ENGINE=InnoDB;\n"]);
        // Keep the state as it was right before the self-test, as if the process had been killed there.
        $saved = null;
        $updater = $this->updater(function (string $key) use (&$saved): void {
            if ($key === 'site.step.selftest') {
                $saved = file_get_contents("$this->var/update/state.json");
            }
        });
        $this->assertTrue($updater->installFile($package, 'test'), $this->explain());
        rename("$this->var/update/backup", "$this->var/update/backup.new");
        file_put_contents(Updater::maintenanceFile($this->var), '{"version":"0.9.5"}');
        file_put_contents("$this->var/update/state.json", $saved);

        $updater = $this->updater();
        $this->assertSame('interrupted', $updater->pending());
        $this->assertFalse($updater->recover());
        $state = Updater::readState($this->var);
        $this->assertSame(['failed', 'site.error.interrupted', true], [$state['status'], $state['error']['key'], $state['rolled_back']]);
        $this->assertSame(App::VERSION, $this->version());
        $this->assertSame([], $db->column("SHOW TABLES LIKE 'update_interrupted'"));
        $this->assertFileDoesNotExist(Updater::maintenanceFile($this->var));
        $this->assertNull($updater->pending());
    }

    public function testChecksStopBeforeAnythingChanges(): void
    {
        // The same version, a package with a path out of the folder, one without its scripts.
        $same = $this->package(App::VERSION);
        $this->assertFalse($this->updater()->installFile($same, 'test'));
        $this->assertSame('site.error.not_newer', Updater::readState($this->var)['error']['key']);
        $this->assertSame(['checks'], array_column(Updater::readState($this->var)['steps'], 'step'));

        $bad = TestEnv::dir() . '/bad.zip';
        ZipWriter::write($bad, ['server/src/App.php' => "<?php const VERSION = '9.0.0';", 'server/../escape.php' => '<?php']);
        $this->assertFalse($this->updater()->installFile($bad, 'test'));
        $this->assertSame('site.error.package', Updater::readState($this->var)['error']['key']);
        $this->assertStringContainsString('unsafe path', Updater::readState($this->var)['error']['params']['reason']);

        ZipWriter::write($bad, ['server/src/App.php' => "<?php const VERSION = '9.0.0';"]);
        $this->assertFalse($this->updater()->installFile($bad, 'test'));
        $this->assertStringContainsString('server/composer.json is missing', Updater::readState($this->var)['error']['params']['reason']);
        $this->assertSame(App::VERSION, $this->version());

        // A folder the site user may not change (unpacked by root once): the update says how to fix it.
        if (function_exists('posix_geteuid') && posix_geteuid() === 0) {
            return; // root may change anything
        }
        chmod("$this->root/src/Domain", 0555);
        $this->assertFalse($this->updater()->installFile($this->package('0.9.6'), 'test'));
        $error = Updater::readState($this->var)['error'];
        $this->assertSame('site.error.owner', $error['key']);
        $this->assertSame("$this->root/src/Domain", $error['params']['path']);
        $this->assertSame(App::VERSION, $this->version());
    }

    public function testDevelopmentCopyIsNeverUpdated(): void
    {
        touch("$this->root/phpunit.xml.dist");
        $this->assertFalse($this->updater()->installFile($this->package('0.9.7'), 'test'));
        $this->assertSame('site.error.dev_copy', Updater::readState($this->var)['error']['key']);
        $this->assertSame(App::VERSION, $this->version());
    }

    // -------------------------------------------------------------------------------------------------------------

    private function updater(?\Closure $say = null): Updater
    {
        return new Updater($this->root, $this->var, fn() => $this->app->db()->pdo, static fn() => [0, '', 'no network in tests'],
            $say ?? function (string $key, array $params): void {
                $this->said[] = [$key, $params];
            }, PHP_BINARY, 0);
    }

    /** A package of the site copy with another version and extra or replaced files. */
    private function package(string $version, array $files = []): string
    {
        $entries = ZipWriter::tree($this->root, 'server/');
        foreach (array_keys($entries) as $name) {
            if (str_starts_with($name, 'server/var/') || $name === 'server/var/' || $name === 'server/config/config.php'
                || str_starts_with($name, 'server/public/.well-known')) {
                unset($entries[$name]);
            }
        }
        $entries['server/src/App.php'] = preg_replace("/const VERSION = '[^']+'/", "const VERSION = '$version'", $entries['server/src/App.php']);
        foreach ($files as $name => $content) {
            $entries['server/' . $name] = $content;
        }
        $file = TestEnv::dir() . "/custos-server-$version-" . bin2hex(random_bytes(2)) . '.zip';
        ZipWriter::write($file, $entries);
        return $file;
    }

    private function version(): string
    {
        preg_match("/const VERSION = '([^']+)'/", (string) file_get_contents("$this->root/src/App.php"), $m);
        return $m[1];
    }

    private function explain(): string
    {
        return json_encode(Updater::readState($this->var), JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    }

    /** @return array{0: int, 1: string} */
    private function php(array $args): array
    {
        $process = proc_open([PHP_BINARY, ...$args], [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['redirect', 1]], $pipes, $this->root);
        fclose($pipes[0]);
        $output = (string) stream_get_contents($pipes[1]);
        fclose($pipes[1]);
        return [proc_close($process), $output];
    }

    /** vendor/autoload.php of the copy: its own classes, PHPMailer from this repository's vendor. */
    private static function autoloader(string $vendor): string
    {
        return '<?php' . "\n" . 'spl_autoload_register(static function (string $class): void {' . "\n"
            . '    foreach ([\'Custos\\\\Tools\\\\\' => __DIR__ . \'/../tools/src/\', \'Custos\\\\\' => __DIR__ . \'/../src/\','
            . ' \'PHPMailer\\\\PHPMailer\\\\\' => ' . var_export($vendor . '/phpmailer/phpmailer/src/', true) . '] as $prefix => $dir) {' . "\n"
            . '        if (str_starts_with($class, $prefix)) {' . "\n"
            . '            $file = $dir . str_replace(\'\\\\\', \'/\', substr($class, strlen($prefix))) . \'.php\';' . "\n"
            . '            if (is_file($file)) {' . "\n"
            . '                require $file;' . "\n"
            . '            }' . "\n"
            . '            return;' . "\n"
            . '        }' . "\n"
            . '    }' . "\n"
            . '});' . "\n"
            . 'require_once __DIR__ . \'/../src/helpers.php\';' . "\n";
    }

    private static function copyTree(string $from, string $to): void
    {
        @mkdir($to, 0755, true);
        foreach (array_diff(scandir($from) ?: [], ['.', '..']) as $item) {
            is_dir("$from/$item") ? self::copyTree("$from/$item", "$to/$item") : copy("$from/$item", "$to/$item");
        }
    }
}
