<?php

declare(strict_types=1);

namespace Custos;

/**
 * Helpers for the command-line scripts in bin/.
 */
final class Cli
{
    public static function ok(string $message): void
    {
        fwrite(STDOUT, "[ok] $message\n");
    }

    public static function warn(string $message): void
    {
        fwrite(STDOUT, "[!!] $message\n");
    }

    public static function info(string $message): void
    {
        fwrite(STDOUT, "     $message\n");
    }

    public static function fail(string $message): never
    {
        fwrite(STDERR, "[error] $message\n");
        exit(1);
    }

    /** Parses --name=value and --flag options. */
    public static function options(array $argv): array
    {
        $options = [];
        foreach (array_slice($argv, 1) as $arg) {
            if (preg_match('/^--([a-z0-9-]+)(?:=(.*))?$/s', $arg, $m)) {
                $options[$m[1]] = $m[2] ?? true;
            }
        }
        return $options;
    }

    public static function prompt(string $question): string
    {
        fwrite(STDOUT, $question);
        $line = fgets(STDIN);
        return $line === false ? '' : trim($line);
    }

    /** Reads a line without echo when the terminal allows it. */
    public static function promptHidden(string $question): string
    {
        fwrite(STDOUT, $question);
        $tty = stream_isatty(STDIN) && function_exists('shell_exec');
        if ($tty) {
            shell_exec('stty -echo 2>/dev/null');
        }
        $line = fgets(STDIN);
        if ($tty) {
            shell_exec('stty echo 2>/dev/null');
            fwrite(STDOUT, "\n");
        }
        return $line === false ? '' : rtrim($line, "\r\n");
    }

    public static function boot(): App
    {
        $root = dirname(__DIR__);
        try {
            return App::boot($root);
        } catch (\Throwable $e) {
            self::fail($e->getMessage());
        }
    }
}
