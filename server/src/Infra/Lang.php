<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * Interface strings from lang/<code>.php. Missing keys fall back to Russian, then to the key itself.
 */
final class Lang
{
    private array $strings;

    public function __construct(private readonly string $dir, public readonly string $code = 'ru')
    {
        $this->strings = $this->read($code) + ($code === 'ru' ? [] : $this->read('ru'));
    }

    /** "{name}" placeholders are replaced from $params. */
    public function get(string $key, array $params = []): string
    {
        $text = $this->strings[$key] ?? $key;
        if ($params !== []) {
            $replace = [];
            foreach ($params as $name => $value) {
                $replace['{' . $name . '}'] = (string) $value;
            }
            $text = strtr($text, $replace);
        }
        return $text;
    }

    public function has(string $key): bool
    {
        return isset($this->strings[$key]);
    }

    private function read(string $code): array
    {
        $file = $this->dir . '/' . basename($code) . '.php';
        return is_file($file) ? (array) require $file : [];
    }
}
