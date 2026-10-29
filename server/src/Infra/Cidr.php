<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * IP allow-lists (section 4): IPv4 and IPv6 addresses and CIDR ranges. An empty list allows everything.
 */
final class Cidr
{
    /**
     * Parses a list typed by a user: entries separated by new lines, commas or spaces.
     *
     * @return list<string> normalized entries
     * @throws \InvalidArgumentException listing the entries that are not addresses or ranges
     */
    public static function parseList(string $text): array
    {
        $entries = [];
        $bad = [];
        foreach (preg_split('/[\s,;]+/', trim($text), -1, PREG_SPLIT_NO_EMPTY) as $entry) {
            $normalized = self::normalize($entry);
            if ($normalized === null) {
                $bad[] = $entry;
            } elseif (!in_array($normalized, $entries, true)) {
                $entries[] = $normalized;
            }
        }
        if ($bad !== []) {
            throw new \InvalidArgumentException(implode(', ', $bad));
        }
        return $entries;
    }

    /** Reads a list stored by parseList(); entries that became invalid are skipped. */
    public static function fromStored(string $text): array
    {
        $entries = [];
        foreach (preg_split('/\s+/', trim($text), -1, PREG_SPLIT_NO_EMPTY) as $entry) {
            if (self::normalize($entry) !== null) {
                $entries[] = $entry;
            }
        }
        return $entries;
    }

    /** @param list<string> $list */
    public static function allows(string $ip, array $list): bool
    {
        if ($list === []) {
            return true;
        }
        foreach ($list as $range) {
            if (self::contains($range, $ip)) {
                return true;
            }
        }
        return false;
    }

    public static function contains(string $range, string $ip): bool
    {
        [$network, $bits] = array_pad(explode('/', $range, 2), 2, null);
        $net = @inet_pton((string) $network);
        $addr = @inet_pton(self::unmapIpv4($ip));
        if ($net === false || $addr === false || strlen($net) !== strlen($addr)) {
            return false;
        }
        $bits = $bits === null ? strlen($net) * 8 : (int) $bits;
        $bytes = intdiv($bits, 8);
        if (strncmp($net, $addr, $bytes) !== 0) {
            return false;
        }
        $rest = $bits % 8;
        if ($rest === 0) {
            return true;
        }
        $mask = (0xff << (8 - $rest)) & 0xff;
        return (ord($net[$bytes]) & $mask) === (ord($addr[$bytes]) & $mask);
    }

    private static function normalize(string $entry): ?string
    {
        [$network, $bits] = array_pad(explode('/', $entry, 2), 2, null);
        $packed = @inet_pton((string) $network);
        if ($packed === false) {
            return null;
        }
        $max = strlen($packed) * 8;
        if ($bits !== null && (!ctype_digit($bits) || (int) $bits > $max)) {
            return null;
        }
        $text = inet_ntop($packed);
        return $bits === null ? $text : $text . '/' . (int) $bits;
    }

    /** PHP-FPM on a dual-stack socket may report IPv4 clients as ::ffff:192.0.2.1. */
    private static function unmapIpv4(string $ip): string
    {
        return preg_match('/^::ffff:(\d+\.\d+\.\d+\.\d+)$/i', $ip, $m) ? $m[1] : $ip;
    }
}
