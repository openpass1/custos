<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Infra\ZipReader;
use Custos\Tests\TestEnv;
use PHPUnit\Framework\TestCase;

/** The reader of update packages (A.10) works without the zip extension: stored and deflated entries. */
final class ZipReaderTest extends TestCase
{
    public function testPackageOfTheTestTool(): void
    {
        $file = TestEnv::dir() . '/package-' . uniqid() . '.zip';
        exec(escapeshellarg(PHP_BINARY) . ' ' . escapeshellarg(dirname(__DIR__, 2) . '/tools/make-update-package.php')
            . ' --version=1.2.3 --out=' . escapeshellarg($file), $output, $code);
        $this->assertSame(0, $code, implode("\n", $output));
        $zip = ZipReader::open($file);
        $this->assertSame(['manifest.json', 'Custos.Service.exe', 'Custos.Gui.exe'], $zip->names());
        $manifest = json_decode((string) $zip->read('manifest.json'), true);
        $this->assertSame('1.2.3', $manifest['version']);
        $this->assertSame(1, $manifest['min_protocol']);
        foreach ($manifest['files'] as $entry) {
            $this->assertSame($entry['sha256'], hash('sha256', (string) $zip->read($entry['path'])));
        }
        $this->assertNull($zip->read('missing.txt'));
    }

    public function testDeflatedEntriesFoldersAndComment(): void
    {
        if (!class_exists(\ZipArchive::class)) {
            $this->markTestSkipped('The zip extension is needed to make a deflated archive.');
        }
        $file = TestEnv::dir() . '/deflated-' . uniqid() . '.zip';
        $text = str_repeat("CUSTOS update package line\n", 2000);
        $archive = new \ZipArchive();
        $this->assertTrue($archive->open($file, \ZipArchive::CREATE | \ZipArchive::OVERWRITE));
        $archive->addEmptyDir('bin');
        $archive->addFromString('bin/readme.txt', $text);
        $archive->setCompressionName('bin/readme.txt', \ZipArchive::CM_DEFLATE);
        $archive->addFromString('manifest.json', '{"version":"2.0.0"}');
        $archive->setArchiveComment('a comment after the directory');
        $archive->close();
        $this->assertLessThan(strlen($text), filesize($file), 'the entry is really compressed');

        $zip = ZipReader::open($file);
        $this->assertTrue($zip->has('bin/'));
        $this->assertTrue($zip->has('bin/readme.txt'));
        $this->assertSame($text, $zip->read('bin/readme.txt'));
        $this->assertSame('{"version":"2.0.0"}', $zip->read('manifest.json'));
    }

    public function testNotAZipFile(): void
    {
        $file = TestEnv::dir() . '/not-a-zip-' . uniqid() . '.zip';
        file_put_contents($file, "MZ this is a program, not an archive\n" . str_repeat('x', 100));
        $this->expectException(\RuntimeException::class);
        ZipReader::open($file);
    }
}
