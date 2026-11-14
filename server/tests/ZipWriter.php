<?php

declare(strict_types=1);

namespace Custos\Tests;

/**
 * Writes a zip of deflated entries without the zip extension of PHP, for test packages.
 */
final class ZipWriter
{
    /** @param array<string, string> $entries name => content; names ending in "/" are folders */
    public static function write(string $file, array $entries): void
    {
        $zip = '';
        $directory = '';
        foreach ($entries as $name => $content) {
            $folder = str_ends_with($name, '/');
            $data = $folder ? '' : (string) gzdeflate($content, 6);
            $method = $folder ? 0 : 8;
            $header = pack('vvvvvVVVvv', 20, 0, $method, 0, 0x21, crc32($content), strlen($data), strlen($content), strlen($name), 0);
            $external = ($folder ? 040755 : 0100644) << 16;
            $directory .= "PK\x01\x02" . pack('v', 0x031e) . $header . pack('vvvVV', 0, 0, 0, $external, strlen($zip)) . $name;
            $zip .= "PK\x03\x04" . $header . $name . $data;
        }
        $zip .= $directory . "PK\x05\x06" . pack('vvvvVVv', 0, 0, count($entries), count($entries), strlen($directory), strlen($zip), 0);
        file_put_contents($file, $zip);
    }

    /**
     * Every file under $dir as entries "$prefix<relative path>", with folder entries, like "zip -r".
     *
     * @return array<string, string>
     */
    public static function tree(string $dir, string $prefix): array
    {
        $entries = [$prefix => ''];
        $items = new \RecursiveIteratorIterator(new \RecursiveDirectoryIterator($dir, \FilesystemIterator::SKIP_DOTS), \RecursiveIteratorIterator::SELF_FIRST);
        foreach ($items as $item) {
            $relative = substr($item->getPathname(), strlen($dir) + 1);
            if ($item->isDir()) {
                $entries[$prefix . $relative . '/'] = '';
            } else {
                $entries[$prefix . $relative] = (string) file_get_contents($item->getPathname());
            }
        }
        return $entries;
    }
}
