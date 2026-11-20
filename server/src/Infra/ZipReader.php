<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * Reads the list of files and small files from a ZIP archive, enough to check an agent update package (A.10), and
 * unpacks a site update package, without the zip extension of PHP. Stored and deflated entries are supported;
 * encrypted and ZIP64 archives are not.
 */
final class ZipReader
{
    private const EOCD = "PK\x05\x06";
    private const CENTRAL = "PK\x01\x02";
    private const LOCAL = "PK\x03\x04";
    private const MAX_ENTRY_BYTES = 4 * 1024 * 1024;

    /** Unpacking a whole archive allows bigger files than read(). */
    private const MAX_EXTRACT_BYTES = 64 * 1024 * 1024;

    /** @var array<string, array{method: int, flags: int, crc: int, compressed: int, size: int, offset: int, external: int}> */
    private array $entries = [];

    /** @param resource $handle */
    private function __construct(private $handle)
    {
    }

    /** @throws \RuntimeException when the file is not a ZIP archive this reader understands */
    public static function open(string $file): self
    {
        $handle = @fopen($file, 'rb');
        if ($handle === false) {
            throw new \RuntimeException('cannot open the file');
        }
        $reader = new self($handle);
        $reader->readDirectory(filesize($file) ?: 0);
        return $reader;
    }

    public function __destruct()
    {
        if (is_resource($this->handle)) {
            fclose($this->handle);
        }
    }

    /** @return list<string> names of the entries, folders included ("bin/") */
    public function names(): array
    {
        return array_keys($this->entries);
    }

    public function has(string $name): bool
    {
        return isset($this->entries[$name]);
    }

    /** Size of an entry when unpacked, or null when there is no such entry. */
    public function size(string $name): ?int
    {
        return isset($this->entries[$name]) ? $this->entries[$name]['size'] : null;
    }

    /** A symbolic link stored by a Unix zip: such entries are never unpacked as files. */
    public function isLink(string $name): bool
    {
        return isset($this->entries[$name]) && (($this->entries[$name]['external'] >> 16) & 0170000) === 0120000;
    }

    /** The content of an entry (at most 4 MB), or null when there is no such entry. */
    public function read(string $name): ?string
    {
        return $this->content($name, self::MAX_ENTRY_BYTES);
    }

    /**
     * Writes an entry of any size to $out in pieces, never holding it in memory (the archive of the program inside the
     * package for Linux, about 40 MB), and checks its CRC at the end. Returns the number of bytes written.
     *
     * @param resource $out
     * @throws \RuntimeException when there is no such entry or it is damaged; part of it may have been written then
     */
    public function copy(string $name, $out): int
    {
        $entry = $this->entries[$name] ?? throw new \RuntimeException("no $name in the archive");
        if ($entry['flags'] & 1) {
            throw new \RuntimeException("$name is encrypted");
        }
        if (!in_array($entry['method'], [0, 8], true)) {
            throw new \RuntimeException("$name uses an unsupported compression method {$entry['method']}");
        }
        fseek($this->handle, $entry['offset']);
        $header = (string) fread($this->handle, 30);
        if (strlen($header) !== 30 || !str_starts_with($header, self::LOCAL)) {
            throw new \RuntimeException("bad local header of $name");
        }
        $local = unpack('vnameLength/vextraLength', substr($header, 26, 4));
        $meta = stream_get_meta_data($this->handle);
        $in = @fopen((string) $meta['uri'], 'rb');
        if ($in === false) {
            throw new \RuntimeException('cannot open the archive again');
        }
        try {
            fseek($in, $entry['offset'] + 30 + $local['nameLength'] + $local['extraLength']);
            if ($entry['method'] === 8 && $entry['size'] > 0) {
                // Raw deflate, as ZIP stores it.
                stream_filter_append($in, 'zlib.inflate', STREAM_FILTER_READ, ['window' => -15]);
            }
            $crc = hash_init('crc32b');
            $left = $entry['size'];
            while ($left > 0) {
                $chunk = fread($in, min(1 << 20, $left));
                if ($chunk === false || $chunk === '') {
                    break;
                }
                hash_update($crc, $chunk);
                if (fwrite($out, $chunk) !== strlen($chunk)) {
                    throw new \RuntimeException("cannot write $name");
                }
                $left -= strlen($chunk);
            }
            if ($left !== 0 || hash_final($crc) !== sprintf('%08x', $entry['crc'])) {
                throw new \RuntimeException("$name is damaged");
            }
            return $entry['size'];
        } finally {
            fclose($in);
        }
    }

    /**
     * Unpacks the entries under $prefix ("server/") into $target, without the prefix. $skip decides about each path
     * (relative to the prefix); folders are created as needed. Paths that would leave $target, links and damaged
     * entries stop the unpacking with an exception.
     *
     * @param callable(string): bool $skip
     * @return int number of files written
     */
    public function extract(string $prefix, string $target, callable $skip): int
    {
        $count = 0;
        foreach ($this->entries as $name => $entry) {
            if (!str_starts_with($name, $prefix) || $name === $prefix) {
                continue;
            }
            $relative = substr($name, strlen($prefix));
            if (!self::safePath($relative)) {
                throw new \RuntimeException("unsafe path in the archive: $name");
            }
            if ($this->isLink($name)) {
                throw new \RuntimeException("$name is a symbolic link");
            }
            if ($skip(rtrim($relative, '/'))) {
                continue;
            }
            $path = $target . '/' . rtrim($relative, '/');
            if (str_ends_with($relative, '/')) {
                if (!is_dir($path) && !@mkdir($path, 0755, true)) {
                    throw new \RuntimeException("cannot create the folder $path");
                }
                continue;
            }
            $dir = dirname($path);
            if (!is_dir($dir) && !@mkdir($dir, 0755, true)) {
                throw new \RuntimeException("cannot create the folder $dir");
            }
            $content = (string) $this->content($name, self::MAX_EXTRACT_BYTES);
            if (@file_put_contents($path, $content) !== strlen($content)) {
                throw new \RuntimeException("cannot write $path");
            }
            $count++;
        }
        return $count;
    }

    /** A relative path without "..", ".", empty parts, backslashes, drive letters or a leading slash. */
    public static function safePath(string $path): bool
    {
        if ($path === '' || str_starts_with($path, '/') || str_contains($path, '\\') || str_contains($path, "\0") || str_contains($path, ':')) {
            return false;
        }
        foreach (explode('/', rtrim($path, '/')) as $part) {
            if ($part === '' || $part === '.' || $part === '..') {
                return false;
            }
        }
        return true;
    }

    private function content(string $name, int $limit): ?string
    {
        $entry = $this->entries[$name] ?? null;
        if ($entry === null) {
            return null;
        }
        if ($entry['flags'] & 1) {
            throw new \RuntimeException("$name is encrypted");
        }
        if ($entry['size'] > $limit || $entry['compressed'] > $limit) {
            throw new \RuntimeException("$name is too large");
        }
        fseek($this->handle, $entry['offset']);
        $header = (string) fread($this->handle, 30);
        if (strlen($header) !== 30 || !str_starts_with($header, self::LOCAL)) {
            throw new \RuntimeException("bad local header of $name");
        }
        $local = unpack('vnameLength/vextraLength', substr($header, 26, 4));
        fseek($this->handle, $entry['offset'] + 30 + $local['nameLength'] + $local['extraLength']);
        $data = $entry['compressed'] === 0 ? '' : (string) fread($this->handle, $entry['compressed']);
        $content = match ($entry['method']) {
            0 => $data,
            8 => $entry['size'] === 0 ? '' : @gzinflate($data, $limit),
            default => throw new \RuntimeException("$name uses an unsupported compression method {$entry['method']}"),
        };
        if ($content === false || strlen($content) !== $entry['size'] || crc32($content) !== $entry['crc']) {
            throw new \RuntimeException("$name is damaged");
        }
        return $content;
    }

    private function readDirectory(int $size): void
    {
        if ($size < 22) {
            throw new \RuntimeException('not a ZIP archive');
        }
        $tail = min($size, 22 + 65535);
        fseek($this->handle, $size - $tail);
        $buffer = (string) fread($this->handle, $tail);
        $pos = strrpos($buffer, self::EOCD);
        if ($pos === false || strlen($buffer) - $pos < 22) {
            throw new \RuntimeException('not a ZIP archive');
        }
        $eocd = unpack('vdisk/vcdDisk/ventriesDisk/ventries/VcdSize/VcdOffset', substr($buffer, $pos + 4, 16));
        if ($eocd['entries'] === 0xFFFF || $eocd['cdOffset'] === 0xFFFFFFFF) {
            throw new \RuntimeException('ZIP64 archives are not supported');
        }
        fseek($this->handle, $eocd['cdOffset']);
        $directory = (string) fread($this->handle, $eocd['cdSize']);
        $offset = 0;
        for ($i = 0; $i < $eocd['entries']; $i++) {
            if (substr($directory, $offset, 4) !== self::CENTRAL || strlen($directory) < $offset + 46) {
                throw new \RuntimeException('damaged ZIP directory');
            }
            $e = unpack('vflags/vmethod/vtime/vdate/Vcrc/Vcompressed/Vsize/vnameLength/vextraLength/vcommentLength/vdisk/vinternal/Vexternal/Voffset',
                substr($directory, $offset + 8, 38));
            $name = substr($directory, $offset + 46, $e['nameLength']);
            $this->entries[str_replace('\\', '/', $name)] = [
                'method' => $e['method'], 'flags' => $e['flags'], 'crc' => $e['crc'], 'compressed' => $e['compressed'], 'size' => $e['size'],
                'offset' => $e['offset'], 'external' => $e['external'],
            ];
            $offset += 46 + $e['nameLength'] + $e['extraLength'] + $e['commentLength'];
        }
    }
}
