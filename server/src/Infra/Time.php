<?php

declare(strict_types=1);

namespace Custos\Infra;

use DateTimeImmutable;
use DateTimeZone;

/**
 * Times are stored in the database as "Y-m-d H:i:s" in UTC and sent over the API as ISO 8601 with "Z" (A.1).
 */
final class Time
{
    public const DB = 'Y-m-d H:i:s';

    public static function now(): string
    {
        return gmdate(self::DB);
    }

    public static function db(int $unix): string
    {
        return gmdate(self::DB, $unix);
    }

    public static function unix(string $db): int
    {
        return (new DateTimeImmutable($db, new DateTimeZone('UTC')))->getTimestamp();
    }

    /** ISO 8601 with a "Z" or an offset, fractions of a second are dropped. Returns null when the value is not such a time. */
    public static function parseIso(string $value): ?string
    {
        if (!preg_match('/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$/', $value)) {
            return null;
        }
        $value = preg_replace('/\.(\d{6})\d+/', '.$1', $value);
        try {
            $time = new DateTimeImmutable($value);
        } catch (\Exception) {
            return null;
        }
        $errors = DateTimeImmutable::getLastErrors();
        if ($errors !== false && ($errors['warning_count'] > 0 || $errors['error_count'] > 0)) {
            return null;
        }
        return $time->setTimezone(new DateTimeZone('UTC'))->format(self::DB);
    }

    public static function iso(?string $db): ?string
    {
        if ($db === null) {
            return null;
        }
        return (new DateTimeImmutable($db, new DateTimeZone('UTC')))->format('Y-m-d\TH:i:s\Z');
    }

    public static function isoNow(): string
    {
        return gmdate('Y-m-d\TH:i:s\Z');
    }
}
