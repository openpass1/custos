<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\Domain\Install;
use PHPUnit\Framework\TestCase;

/** The parts of the install command for Linux that need no site: the token, the quoting, the archives of a package. */
final class InstallTest extends TestCase
{
    public function testTokenComesFromTheKeyAndHidesIt(): void
    {
        $key = 'K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ';
        $token = Install::token($key);
        $this->assertMatchesRegularExpression('/^[0-9a-f]{32}$/', $token);
        $this->assertSame($token, Install::token('k7qhm2xarv4ntb6ewz3plc5dq'), 'the key as it may be typed');
        $this->assertNotSame($token, Install::token('K7QHM-2XARV-4NTB6-EWZ3P-LC5DA'), 'a new key, a new address');
        $this->assertStringNotContainsStringIgnoringCase('K7QHM', $token);
    }

    public function testQuoteKeepsAnyTextOneWordForBash(): void
    {
        $this->assertSame("'http://site.example/x'", Install::quote('http://site.example/x'));
        foreach (["it's", 'a "b" $c `d` \\e', "two\nlines", '#K#F', ''] as $text) {
            $out = shell_exec('printf %s ' . Install::quote($text));
            $this->assertSame($text, (string) $out, $text);
        }
    }

    public function testArchiveSumsComeFromTheManifest(): void
    {
        $sha = str_repeat('ab', 32);
        $manifest = json_encode(['version' => '1.0.0', 'edition' => 'linux', 'files' => [
            ['path' => 'custos-linux-x64.tar.gz', 'sha256' => strtoupper($sha)],
            ['path' => 'custos-linux-arm64.tar.gz', 'sha256' => 'not a sum'],
            ['path' => 'readme.txt', 'sha256' => $sha],
            'custos-linux-arm64.tar.gz',
        ]]);
        $this->assertSame(['x64' => $sha], Install::archiveSums(['manifest' => $manifest]));
        $this->assertSame([], Install::archiveSums(['manifest' => '{}']));
        $this->assertSame([], Install::archiveSums([]));
        $this->assertSame('custos-linux-arm64.tar.gz', Install::archiveName('arm64'));
    }
}
