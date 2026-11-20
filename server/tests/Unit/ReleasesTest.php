<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\Releases;
use Custos\Infra\DbSnapshot;
use Custos\Infra\Updater;
use Custos\Infra\ZipReader;
use Custos\Tests\ZipWriter;
use PHPUnit\Framework\TestCase;

/** The parts of the site update that need no database: releases, migrations, PHP constraints, unpacking. */
final class ReleasesTest extends TestCase
{
    public function testNewestReleaseOfEachPart(): void
    {
        $asset = fn(string $name, string $digest = '') => ['name' => $name, 'browser_download_url' => "https://example.test/$name", 'size' => 10]
            + ($digest === '' ? [] : ['digest' => $digest]);
        $sha = str_repeat('ab', 32);
        $releases = [
            ['tag_name' => 'server-v1.10.0', 'draft' => true, 'assets' => [$asset('custos-server-1.10.0.zip')]],
            ['tag_name' => 'server-v1.9.0', 'prerelease' => true, 'assets' => [$asset('custos-server-1.9.0.zip')]],
            ['tag_name' => 'client-v2.0.0', 'html_url' => 'https://example.test/client-v2.0.0', 'assets' => [$asset('custos-2.0.0.zip', "sha256:$sha")]],
            ['tag_name' => 'server-v1.2.0', 'assets' => []],
            ['tag_name' => 'server-v1.1.0', 'html_url' => 'https://example.test/server-v1.1.0', 'published_at' => '2026-11-14T10:00:00Z',
             'assets' => [$asset('custos-server-1.1.0.zip', 'sha256:' . strtoupper($sha)), $asset('SHA256SUMS.txt')]],
            ['tag_name' => 'server-v1.0.10', 'assets' => [$asset('custos-server-1.0.10.zip')]],
            ['tag_name' => 'v3.0.0', 'assets' => [$asset('custos-server-3.0.0.zip')]],
            'not a release',
        ];
        $server = Releases::pick($releases, 'server');
        $this->assertSame('1.1.0', $server['version'], 'by version number; drafts, pre-releases and releases without the package are skipped');
        $this->assertSame('custos-server-1.1.0.zip', $server['zip_name']);
        $this->assertSame('https://example.test/custos-server-1.1.0.zip', $server['zip_url']);
        $this->assertSame($sha, $server['sha256']);
        $this->assertSame('https://example.test/SHA256SUMS.txt', $server['sums_url']);
        $this->assertSame('https://example.test/server-v1.1.0', $server['url']);

        $client = Releases::pick($releases, 'client');
        $this->assertSame(['2.0.0', 'custos-2.0.0.zip', null], [$client['version'], $client['zip_name'], $client['sums_url']]);
        $this->assertNull(Releases::pick([], 'server'));

        // The edition for Windows 7 and 8.1: tags client-legacy-v*, never taken for the standard program and back.
        $releases[] = ['tag_name' => 'client-legacy-v2.1.0', 'assets' => [$asset('custos-legacy-2.1.0.zip')]];
        $releases[] = ['tag_name' => 'client-legacy-v1.0.0', 'assets' => [$asset('custos-legacy-1.0.0.zip')]];
        $releases[] = ['tag_name' => 'client-legacy-v3.0.0', 'assets' => [$asset('custos-3.0.0.zip')]];
        $this->assertSame('2.0.0', Releases::pick($releases, 'client')['version']);
        $legacy = Releases::pick($releases, 'legacy');
        $this->assertSame(['2.1.0', 'client-legacy-v2.1.0', 'custos-legacy-2.1.0.zip'], [$legacy['version'], $legacy['tag'], $legacy['zip_name']]);

        // CUSTOS for macOS: its own package in the same client-v* releases; a release without it is passed over.
        $this->assertNull(Releases::pick($releases, 'macos'), 'no Mac package yet');
        $releases[] = ['tag_name' => 'client-v2.2.0', 'assets' => [$asset('custos-2.2.0.zip'), $asset('custos-macos-2.2.0.zip', "sha256:$sha")]];
        $releases[] = ['tag_name' => 'client-v2.3.0', 'assets' => [$asset('custos-2.3.0.zip')]];
        $mac = Releases::pick($releases, 'macos');
        $this->assertSame(['2.2.0', 'client-v2.2.0', 'custos-macos-2.2.0.zip', $sha], [$mac['version'], $mac['tag'], $mac['zip_name'], $mac['sha256']]);
        $this->assertSame('custos-2.3.0.zip', Releases::pick($releases, 'client')['zip_name'], 'the Mac package is never taken for Windows');
        $this->assertSame(['agent', 'legacy', 'macos', 'linux'], array_map(Releases::kind(...), Releases::PROGRAM_PARTS));

        // CUSTOS for Linux: its own package in the same releases; the installers are kept for the page Install.
        $this->assertNull(Releases::pick($releases, 'linux'), 'no Linux package yet');
        $releases[] = ['tag_name' => 'client-v2.4.0', 'html_url' => 'https://example.test/r/2.4.0', 'assets' => [
            $asset('custos-2.4.0.zip'), $asset('custos-linux-2.4.0.zip'), $asset('custos-linux-x64.tar.gz'),
            $asset('CUSTOS-Setup-2.4.0.exe'), $asset('Custos-2.4.0.pkg'),
        ]];
        $linux = Releases::pick($releases, 'linux');
        $this->assertSame(['2.4.0', 'custos-linux-2.4.0.zip'], [$linux['version'], $linux['zip_name']]);
        $client = Releases::pick($releases, 'client');
        $this->assertSame('custos-2.4.0.zip', $client['zip_name'], 'the Linux package is never taken for Windows');
        $this->assertSame(['CUSTOS-Setup-2.4.0.exe', 'Custos-2.4.0.pkg'], array_keys($client['installers']));
        $this->assertSame('https://example.test/CUSTOS-Setup-2.4.0.exe', $client['installers']['CUSTOS-Setup-2.4.0.exe']);
    }

    public function testTablesThatMigrationsChange(): void
    {
        $this->assertSame(['settings', 'tasks', 'runs', 'update_test', 'agents', 'commands', 'a', 'b', 'c', 'old_name', 'new_name'], DbSnapshot::touchedTables([
            "UPDATE settings SET value = '20' WHERE name = 'heartbeat_interval_s' AND value = '60'",
            "UPDATE tasks t JOIN runs r ON r.task_id = t.id\n SET t.last_run_id = r.id",
            'CREATE TABLE IF NOT EXISTS `update_test` (id INT NOT NULL)',
            'ALTER TABLE agents ADD COLUMN x INT NULL',
            'INSERT IGNORE INTO commands (id) VALUES (1)',
            'DELETE FROM a WHERE 1',
            'CREATE INDEX ix_b ON b (x)',
            'DROP TABLE IF EXISTS c',
            'RENAME TABLE old_name TO new_name',
            'TRUNCATE TABLE settings',
        ]));
        $this->assertNull(DbSnapshot::touchedTables(['SET @x = 1']), 'not understood: every table is copied');
        $this->assertNull(DbSnapshot::touchedTables(['CREATE VIEW v AS SELECT 1']));
        $this->assertSame([], DbSnapshot::touchedTables([]));

        // Every migration of the site is understood, so an update copies only what it changes.
        foreach (glob(dirname(__DIR__, 2) . '/migrations/*.sql') as $file) {
            $this->assertIsArray(DbSnapshot::touchedTables(\Custos\Infra\Migrator::statements((string) file_get_contents($file))), basename($file));
        }
    }

    public function testPhpConstraints(): void
    {
        foreach ([['>=8.3', '8.5.1', true], ['>=8.3', '8.2.20', false], ['^8.3', '8.4.0', true], ['^8.3', '9.0.0', false],
                  ['~8.3.0', '8.3.9', true], ['~8.3.0', '8.4.0', false], ['>=8.3 <9', '8.5.0', true], ['>=8.3 <9', '9.1.0', false],
                  ['^8.3 || ^9.0', '9.2.0', true], ['8.4.*', '8.4.7', true], ['8.4.*', '8.5.0', false], ['*', '7.0.0', true],
                  ['>=8.3', '8.5.0-dev', true]] as [$constraint, $version, $ok]) {
            $this->assertSame($ok, Updater::phpSatisfies($constraint, $version), "$constraint against $version");
        }
    }

    public function testUnpackingStaysInsideItsFolder(): void
    {
        $this->assertTrue(ZipReader::safePath('src/App.php'));
        $this->assertTrue(ZipReader::safePath('vendor/'));
        foreach (['', '/etc/passwd', '../x', 'a/../../x', 'a//b', './a', 'C:/x', "a\0b"] as $path) {
            $this->assertFalse(ZipReader::safePath($path), var_export($path, true));
        }

        $dir = sys_get_temp_dir() . '/custos-unpack-' . getmypid();
        $zip = "$dir.zip";
        $big = random_bytes(5 * 1024 * 1024);
        ZipWriter::write($zip, ['server/' => '', 'server/a/' => '', 'server/a/b.txt' => 'b', 'server/big.bin' => $big,
            'server/var/x' => 'never', 'other/c.txt' => 'not under server/']);
        try {
            $count = ZipReader::open($zip)->extract('server/', $dir, fn(string $path) => $path === 'var' || str_starts_with($path, 'var/'));
            $this->assertSame(2, $count);
            $this->assertSame('b', file_get_contents("$dir/a/b.txt"));
            $this->assertSame($big, file_get_contents("$dir/big.bin"), 'files over the 4 MB limit of read() unpack too');
            $this->assertFileDoesNotExist("$dir/var/x");
            $this->assertFileDoesNotExist("$dir/c.txt");

            // A damaged entry (its checksum does not match) stops the unpacking.
            $bytes = (string) file_get_contents($zip);
            file_put_contents($zip, str_replace(gzdeflate('b', 6) . 'PK', gzdeflate('c', 6) . 'PK', $bytes));
            $this->expectException(\RuntimeException::class);
            ZipReader::open($zip)->extract('server/', "$dir-2", fn() => false);
        } finally {
            Updater::removeTree($dir);
            Updater::removeTree("$dir-2");
            @unlink($zip);
        }
    }
}
