<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * Reads the list of files and small files from a ZIP archive, enough to check an agent update package (A.10)
 * without the zip extension of PHP. Stored and deflated entries are supported; encrypted and ZIP64 archives are not.
 */
final class ZipReader
{
    private const EOCD = "PK\x05\x06";
    private const CENTRAL = "PK\x01\x02";
    private const LOCAL = "PK\x03\x04";
    private const MAX_ENTRY_BYTES = 4 * 1024 * 1024;

    /** @var array<string, array{method: int, flags: int, compressed: int, size: int, offset: int}> */
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

    /** The content of an entry (at most 4 MB), or null when there is no such entry. */
    public function read(string $name): ?string
    {
        $entry = $this->entries[$name] ?? null;
        if ($entry === null) {
            return null;
        }
        if ($entry['flags'] & 1) {
            throw new \RuntimeException("$name is encrypted");
        }
        if ($entry['size'] > self::MAX_ENTRY_BYTES || $entry['compressed'] > self::MAX_ENTRY_BYTES) {
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
            8 => @gzinflate($data, self::MAX_ENTRY_BYTES),
            default => throw new \RuntimeException("$name uses an unsupported compression method {$entry['method']}"),
        };
        if ($content === false || strlen($content) !== $entry['size']) {
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
                'method' => $e['method'], 'flags' => $e['flags'], 'compressed' => $e['compressed'], 'size' => $e['size'], 'offset' => $e['offset'],
            ];
            $offset += 46 + $e['nameLength'] + $e['extraLength'] + $e['commentLength'];
        }
    }
}
