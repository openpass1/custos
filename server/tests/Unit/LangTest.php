<?php

declare(strict_types=1);

namespace Custos\Tests\Unit;

use Custos\App;
use PHPUnit\Framework\TestCase;

/** Every language has every Russian string with the same placeholders, and the code asks only for strings that exist. */
final class LangTest extends TestCase
{
    public function testLanguagesHaveTheSameKeysAndPlaceholders(): void
    {
        $ru = self::load('ru');
        foreach (App::LANGUAGES as $code) {
            $lang = self::load($code);
            $this->assertSame([], array_keys(array_diff_key($ru, $lang)), "$code: missing strings");
            $this->assertSame([], array_keys(array_diff_key($lang, $ru)), "$code: strings that Russian does not have");
            foreach ($ru as $key => $text) {
                $this->assertSame(self::placeholders($text), self::placeholders($lang[$key]), "$code: placeholders of $key");
            }
            $this->assertNotSame('', $lang['lang.name']);
        }
    }

    public function testNoKeyIsWrittenTwice(): void
    {
        foreach (App::LANGUAGES as $code) {
            preg_match_all("/^\s*'([^']+)' =>/m", (string) file_get_contents(self::file($code)), $m);
            $twice = array_keys(array_filter(array_count_values($m[1]), fn(int $n) => $n > 1));
            $this->assertSame([], $twice, "$code: keys written twice (the later one wins silently)");
        }
    }

    public function testCodeUsesOnlyExistingStrings(): void
    {
        $ru = self::load('ru');
        $root = dirname(__DIR__, 2);
        $missing = [];
        foreach (['src', 'templates'] as $dir) {
            $files = new \RecursiveIteratorIterator(new \RecursiveDirectoryIterator("$root/$dir", \FilesystemIterator::SKIP_DOTS));
            foreach ($files as $file) {
                if ($file->getExtension() !== 'php') {
                    continue;
                }
                preg_match_all("/\\bt\\(\\s*'([a-z0-9_.]+)'\\s*[,)]/", (string) file_get_contents($file->getPathname()), $m);
                foreach ($m[1] as $key) {
                    if (!isset($ru[$key])) {
                        $missing[] = substr($file->getPathname(), strlen($root) + 1) . ": $key";
                    }
                }
            }
        }
        $this->assertSame([], $missing);
    }

    private static function file(string $code): string
    {
        return dirname(__DIR__, 2) . "/lang/$code.php";
    }

    /** @return array<string, string> */
    private static function load(string $code): array
    {
        return require self::file($code);
    }

    /** @return list<string> */
    private static function placeholders(string $text): array
    {
        preg_match_all('/\{[a-z_]+\}/', $text, $m);
        $list = array_values(array_unique($m[0]));
        sort($list);
        return $list;
    }
}
