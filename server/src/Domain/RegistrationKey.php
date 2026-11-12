<?php

declare(strict_types=1);

namespace Custos\Domain;

/**
 * The agent registration key (A.3): 25 random Base32 characters, shown in groups of five.
 */
final class RegistrationKey
{
    public const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
    public const LENGTH = 25;

    /** A new key in normalized form (no dashes). */
    public static function generate(): string
    {
        $key = '';
        for ($i = 0; $i < self::LENGTH; $i++) {
            $key .= self::ALPHABET[random_int(0, 31)];
        }
        return $key;
    }

    /** Removes spaces and dashes and converts to upper case, as the agent does before computing the HMAC. */
    public static function normalize(string $key): string
    {
        return strtoupper(str_replace([' ', '-'], '', $key));
    }

    /** K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ */
    public static function format(string $key): string
    {
        return implode('-', str_split(self::normalize($key), 5));
    }

    /** The registration code typed into the agent installer: key#server fingerprint. */
    public static function code(string $key, string $serverFingerprint): string
    {
        return self::format($key) . '#' . $serverFingerprint;
    }

    /**
     * The site address and the registration code in one line, pasted into the program on a machine at once:
     * http://custos.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#1A2B-3C4D-5E6F-7A8B. The address ends at the first "#" (A.3).
     */
    public static function connection(string $siteUrl, string $code): string
    {
        return rtrim($siteUrl, '/') . '#' . $code;
    }
}
