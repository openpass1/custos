<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * Values from config/config.php. The CUSTOS_CONFIG environment variable may point to another file (tests, development).
 */
final class Config
{
    private const DEFAULTS = [
        'url' => '',
        'timezone' => 'UTC',
        'trusted_proxies' => [],
    ];

    private function __construct(private readonly array $values, public readonly string $root)
    {
    }

    public static function load(string $root): self
    {
        $file = getenv('CUSTOS_CONFIG') ?: $root . '/config/config.php';
        if (!is_file($file)) {
            throw new \RuntimeException(
                "Configuration file $file not found. Copy config/config.example.php to config/config.php and fill it in."
            );
        }
        $values = require $file;
        if (!is_array($values)) {
            throw new \RuntimeException("Configuration file $file must return an array.");
        }
        return self::fromArray($values, $root);
    }

    public static function fromArray(array $values, string $root): self
    {
        return new self($values + self::DEFAULTS, $root);
    }

    /** Reads a value by a dotted path such as "db.host". */
    public function get(string $key, mixed $default = null): mixed
    {
        $value = $this->values;
        foreach (explode('.', $key) as $part) {
            if (!is_array($value) || !array_key_exists($part, $value)) {
                return $default;
            }
            $value = $value[$part];
        }
        return $value;
    }

    public function db(): array
    {
        $db = $this->get('db');
        if (!is_array($db)) {
            throw new \RuntimeException('The "db" section is missing in the configuration.');
        }
        return $db;
    }

    /** Server keys (A.2), outside the web root. */
    public function keysDir(): string
    {
        return (string) $this->get('keys_dir', $this->root . '/config/keys');
    }

    /** Sessions, logs and locks. */
    public function varDir(): string
    {
        return (string) $this->get('var_dir', $this->root . '/var');
    }

    public function timezone(): string
    {
        return (string) $this->get('timezone');
    }

    public function url(): string
    {
        return rtrim((string) $this->get('url'), '/');
    }

    /** @return list<string> */
    public function trustedProxies(): array
    {
        return array_values(array_map('strval', (array) $this->get('trusted_proxies')));
    }
}
