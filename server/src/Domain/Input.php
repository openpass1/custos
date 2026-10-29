<?php

declare(strict_types=1);

namespace Custos\Domain;

use Custos\Infra\Time;

/**
 * Typed access to a decoded JSON object from an agent. Every getter throws InvalidInput naming the field,
 * which the API turns into 400 bad_request.
 */
final class Input
{
    private const UUID = '/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i';

    public function __construct(private readonly array $data, private readonly string $path = '')
    {
    }

    public static function fromJson(string $json): self
    {
        try {
            $data = json_decode($json, true, 64, JSON_THROW_ON_ERROR | JSON_BIGINT_AS_STRING);
        } catch (\JsonException $e) {
            throw new InvalidInput('body is not valid JSON: ' . $e->getMessage());
        }
        if (!is_array($data) || ($data !== [] && array_is_list($data))) {
            throw new InvalidInput('body must be a JSON object');
        }
        return new self($data);
    }

    public function has(string $key): bool
    {
        return array_key_exists($key, $this->data) && $this->data[$key] !== null;
    }

    public function raw(string $key): mixed
    {
        return $this->data[$key] ?? null;
    }

    public function string(string $key, int $maxLength = 255, bool $nullable = false): ?string
    {
        $value = $this->value($key, $nullable);
        if ($value === null) {
            return null;
        }
        if (!is_string($value)) {
            throw $this->error($key, 'must be a string');
        }
        if (!mb_check_encoding($value, 'UTF-8')) {
            throw $this->error($key, 'must be UTF-8');
        }
        if (mb_strlen($value) > $maxLength) {
            throw $this->error($key, "is longer than $maxLength characters");
        }
        return $value;
    }

    public function int(string $key, bool $nullable = false, int $min = 0): ?int
    {
        $value = $this->value($key, $nullable);
        if ($value === null) {
            return null;
        }
        if (!is_int($value)) {
            throw $this->error($key, 'must be an integer');
        }
        if ($value < $min) {
            throw $this->error($key, "must be at least $min");
        }
        return $value;
    }

    public function number(string $key, bool $nullable = false, float $min = 0, float $max = PHP_FLOAT_MAX): ?float
    {
        $value = $this->value($key, $nullable);
        if ($value === null) {
            return null;
        }
        if (!is_int($value) && !is_float($value)) {
            throw $this->error($key, 'must be a number');
        }
        if ($value < $min || $value > $max) {
            throw $this->error($key, "must be between $min and $max");
        }
        return (float) $value;
    }

    public function bool(string $key, bool $nullable = false): ?bool
    {
        $value = $this->value($key, $nullable);
        if ($value === null) {
            return null;
        }
        if (!is_bool($value)) {
            throw $this->error($key, 'must be true or false');
        }
        return $value;
    }

    /** UUID in lower case. */
    public function uuid(string $key, bool $nullable = false): ?string
    {
        $value = $this->string($key, 36, $nullable);
        if ($value === null) {
            return null;
        }
        if (!preg_match(self::UUID, $value)) {
            throw $this->error($key, 'must be a UUID');
        }
        return strtolower($value);
    }

    /** ISO 8601 time, returned as UTC "Y-m-d H:i:s". */
    public function time(string $key, bool $nullable = false): ?string
    {
        $value = $this->string($key, 40, $nullable);
        if ($value === null) {
            return null;
        }
        return Time::parseIso($value) ?? throw $this->error($key, 'must be an ISO 8601 time such as 2026-10-12T21:00:00Z');
    }

    /** @param list<string> $allowed */
    public function enum(string $key, array $allowed, bool $nullable = false): ?string
    {
        $value = $this->string($key, 64, $nullable);
        if ($value !== null && !in_array($value, $allowed, true)) {
            throw $this->error($key, 'must be one of: ' . implode(', ', $allowed));
        }
        return $value;
    }

    /** A short code such as a status name: lower-case letters, digits and "_". */
    public function code(string $key, bool $nullable = false): ?string
    {
        $value = $this->string($key, 16, $nullable);
        if ($value !== null && !preg_match('/^[a-z0-9_]+$/', $value)) {
            throw $this->error($key, 'must be a code of lower-case letters');
        }
        return $value;
    }

    public function object(string $key, bool $nullable = false): ?self
    {
        $value = $this->value($key, $nullable);
        if ($value === null) {
            return null;
        }
        if (!is_array($value) || ($value !== [] && array_is_list($value))) {
            throw $this->error($key, 'must be an object');
        }
        return new self($value, $this->path . $key . '.');
    }

    /** @return list<mixed> a missing or null list is empty */
    public function list(string $key): array
    {
        $value = $this->data[$key] ?? [];
        if (!is_array($value) || !array_is_list($value)) {
            throw $this->error($key, 'must be an array');
        }
        return $value;
    }

    /** @return list<self> */
    public function objects(string $key): array
    {
        $result = [];
        foreach ($this->list($key) as $i => $item) {
            if (!is_array($item) || ($item !== [] && array_is_list($item))) {
                throw $this->error("{$key}[$i]", 'must be an object');
            }
            $result[] = new self($item, $this->path . "{$key}[$i].");
        }
        return $result;
    }

    /** @return list<string> */
    public function uuids(string $key): array
    {
        $result = [];
        foreach ($this->list($key) as $i => $item) {
            if (!is_string($item) || !preg_match(self::UUID, $item)) {
                throw $this->error("{$key}[$i]", 'must be a UUID');
            }
            $result[] = strtolower($item);
        }
        return $result;
    }

    public function toArray(): array
    {
        return $this->data;
    }

    private function value(string $key, bool $nullable): mixed
    {
        $value = $this->data[$key] ?? null;
        if ($value === null && !$nullable) {
            throw $this->error($key, 'is required');
        }
        return $value;
    }

    private function error(string $key, string $message): InvalidInput
    {
        return new InvalidInput($this->path . $key . ' ' . $message);
    }
}
