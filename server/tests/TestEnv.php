<?php

declare(strict_types=1);

namespace Custos\Tests;

use Custos\App;
use Custos\Domain\RegistrationKey;
use Custos\Http\Request;
use Custos\Infra\Config;
use Custos\Infra\Db;
use Custos\Infra\Migrator;
use PHPUnit\Framework\TestCase;

/**
 * A test installation: an empty database, server keys and folders in a temporary directory.
 *
 * Database settings come from CUSTOS_TEST_DB_HOST, _PORT, _NAME, _USER, _PASSWORD (defaults: 127.0.0.1, 3306,
 * custos_test, root, empty password). Everything in that database is dropped. Tests that need it are skipped
 * when it cannot be reached; CI runs PHPUnit with --fail-on-skipped.
 */
final class TestEnv
{
    private static ?string $dir = null;

    public static function db(): array
    {
        return [
            'host' => getenv('CUSTOS_TEST_DB_HOST') ?: '127.0.0.1',
            'port' => (int) (getenv('CUSTOS_TEST_DB_PORT') ?: 3306),
            'name' => getenv('CUSTOS_TEST_DB_NAME') ?: 'custos_test',
            'user' => getenv('CUSTOS_TEST_DB_USER') ?: 'root',
            'password' => getenv('CUSTOS_TEST_DB_PASSWORD') ?: '',
        ];
    }

    /** A temporary directory for this test process; server keys are created once and reused. */
    public static function dir(): string
    {
        if (self::$dir === null) {
            self::$dir = sys_get_temp_dir() . '/custos-tests-' . getmypid();
            @mkdir(self::$dir, 0700, true);
            if (getenv('CUSTOS_TEST_KEEP') !== '1') {
                register_shutdown_function(static fn() => self::removeTree(self::$dir));
            }
        }
        return self::$dir;
    }

    public static function config(): array
    {
        return [
            'db' => self::db(),
            'url' => 'http://custos.test',
            'timezone' => 'UTC',
            'keys_dir' => self::dir() . '/keys',
            'var_dir' => self::dir() . '/var',
        ];
    }

    /** Writes the configuration as a file for scripts started in a separate process (CUSTOS_CONFIG). */
    public static function configFile(): string
    {
        $file = self::dir() . '/config.php';
        file_put_contents($file, '<?php return ' . var_export(self::config(), true) . ';');
        return $file;
    }

    /** Drops every table of the test database. Skips the test when the database is not reachable. */
    public static function emptyDatabase(TestCase $test): Db
    {
        try {
            $db = Db::connect(self::db());
        } catch (\PDOException $e) {
            $test->markTestSkipped('Test database is not available: ' . $e->getMessage());
        }
        $db->pdo->exec('SET FOREIGN_KEY_CHECKS = 0');
        foreach ($db->column('SHOW TABLES') as $table) {
            $db->pdo->exec("DROP TABLE `$table`");
        }
        $db->pdo->exec('SET FOREIGN_KEY_CHECKS = 1');
        return $db;
    }

    /** A freshly installed application: schema, server keys and a registration key. */
    public static function app(TestCase $test): App
    {
        $db = self::emptyDatabase($test);
        $root = dirname(__DIR__);
        (new Migrator($db, $root . '/migrations'))->migrate(fn() => null);
        $app = new App(Config::fromArray(self::config(), $root));
        $app->keys()->create();
        $app->settings()->set('registration_key', RegistrationKey::generate());
        // Cron passes would ask GitHub for 7-Zip releases; tests that need the check turn it on with a fake HTTP client.
        $app->settings()->set('sevenzip_check', '0');
        $app->settings()->set('releases_check', '0');
        return $app;
    }

    /** HTTP transport for AgentClient that calls the application in-process. */
    public static function transport(App $app, string $ip = '192.0.2.10'): \Closure
    {
        return static function (string $method, string $url, array $headers, string $body) use ($app, $ip): array {
            $parts = parse_url($url);
            $uri = $parts['path'] . (isset($parts['query']) ? '?' . $parts['query'] : '');
            $lower = ['content-length' => (string) strlen($body)];
            foreach ($headers as $name => $value) {
                $lower[strtolower($name)] = (string) $value;
            }
            $response = $app->handle(new Request($method, $uri, $lower, $ip, body: $body));
            $responseHeaders = [];
            foreach ($response->headers as $name => $value) {
                $responseHeaders[strtolower($name)] = $value;
            }
            return [$response->status, $responseHeaders, $response->content()];
        };
    }

    /** Agent keys are slow to make; tests share a few. */
    public static function agentKey(int $n): \OpenSSLAsymmetricKey
    {
        static $keys = [];
        if (!isset($keys[$n])) {
            $file = self::dir() . "/agent-$n.key";
            if (!is_file($file)) {
                $key = openssl_pkey_new(['private_key_bits' => 3072, 'private_key_type' => OPENSSL_KEYTYPE_RSA]);
                openssl_pkey_export($key, $pem);
                file_put_contents($file, $pem);
            }
            $keys[$n] = openssl_pkey_get_private((string) file_get_contents($file));
        }
        return $keys[$n];
    }

    private static function removeTree(?string $dir): void
    {
        if ($dir === null || !is_dir($dir)) {
            return;
        }
        $items = new \RecursiveIteratorIterator(
            new \RecursiveDirectoryIterator($dir, \FilesystemIterator::SKIP_DOTS),
            \RecursiveIteratorIterator::CHILD_FIRST
        );
        foreach ($items as $item) {
            $item->isDir() ? @rmdir($item->getPathname()) : @unlink($item->getPathname());
        }
        @rmdir($dir);
    }
}
