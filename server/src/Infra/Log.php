<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * Application log: var/log/custos-YYYY-MM.log. Falls back to the PHP error log when the directory is not writable.
 */
final class Log
{
    private static ?string $dir = null;

    public static function setDir(string $dir): void
    {
        self::$dir = $dir;
    }

    public static function info(string $message): void
    {
        self::write('info', $message);
    }

    public static function warning(string $message): void
    {
        self::write('warning', $message);
    }

    public static function error(string $message): void
    {
        self::write('error', $message);
    }

    private static function write(string $level, string $message): void
    {
        $line = gmdate('Y-m-d\TH:i:s\Z') . ' ' . strtoupper($level) . ' ' . str_replace(["\r", "\n"], ['', "\n    "], $message) . "\n";
        if (self::$dir !== null) {
            if (!is_dir(self::$dir)) {
                @mkdir(self::$dir, 0750, true);
            }
            if (@file_put_contents(self::$dir . '/custos-' . gmdate('Y-m') . '.log', $line, FILE_APPEND | LOCK_EX) !== false) {
                return;
            }
        }
        error_log('CUSTOS ' . rtrim($line));
    }
}
