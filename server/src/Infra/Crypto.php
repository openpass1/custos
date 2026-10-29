<?php

declare(strict_types=1);

namespace Custos\Infra;

use OpenSSLAsymmetricKey;

/**
 * Cryptography of the protocol (A.2): RSA-3072 keys, RSASSA-PKCS1-v1_5 + SHA-256 signatures,
 * key fingerprints, HMAC-SHA256 and the hybrid encryption of secrets sent to agents.
 */
final class Crypto
{
    public const KEY_BITS = 3072;

    /** @return array{private: string, public: string} PEM texts */
    public static function generateKeyPair(): array
    {
        $key = openssl_pkey_new(['private_key_bits' => self::KEY_BITS, 'private_key_type' => OPENSSL_KEYTYPE_RSA]);
        if ($key === false || !openssl_pkey_export($key, $private)) {
            throw new \RuntimeException('Cannot generate an RSA key: ' . (openssl_error_string() ?: 'unknown OpenSSL error'));
        }
        return ['private' => $private, 'public' => self::publicPem($key)];
    }

    /** Loads an RSA public key from PEM; null when the text is not one. */
    public static function loadPublicKey(string $pem): ?OpenSSLAsymmetricKey
    {
        $key = @openssl_pkey_get_public($pem);
        if ($key === false) {
            return null;
        }
        $details = openssl_pkey_get_details($key);
        if ($details === false || $details['type'] !== OPENSSL_KEYTYPE_RSA) {
            return null;
        }
        return $key;
    }

    public static function keyBits(OpenSSLAsymmetricKey $key): int
    {
        $details = openssl_pkey_get_details($key);
        return $details === false ? 0 : (int) $details['bits'];
    }

    /** Public key as PEM (SubjectPublicKeyInfo). Works for private keys too. */
    public static function publicPem(OpenSSLAsymmetricKey $key): string
    {
        $details = openssl_pkey_get_details($key);
        if ($details === false) {
            throw new \RuntimeException('Cannot read the key.');
        }
        return $details['key'];
    }

    /** DER of the SubjectPublicKeyInfo, the input of the fingerprint. */
    public static function publicDer(OpenSSLAsymmetricKey $key): string
    {
        $pem = self::publicPem($key);
        $base64 = preg_replace('/-----[^-]+-----|\s+/', '', $pem);
        return base64_decode($base64, true) ?: throw new \RuntimeException('Bad public key PEM.');
    }

    /** First 16 hex characters of SHA-256 of the DER, in groups of 4: 3F2A-91C0-77DE-0B14. */
    public static function fingerprint(OpenSSLAsymmetricKey $key): string
    {
        $hex = strtoupper(substr(hash('sha256', self::publicDer($key)), 0, 16));
        return implode('-', str_split($hex, 4));
    }

    public static function sign(string $data, OpenSSLAsymmetricKey $privateKey): string
    {
        if (!openssl_sign($data, $signature, $privateKey, OPENSSL_ALGO_SHA256)) {
            throw new \RuntimeException('Cannot sign: ' . (openssl_error_string() ?: 'unknown OpenSSL error'));
        }
        return base64_encode($signature);
    }

    public static function verify(string $data, string $signatureBase64, OpenSSLAsymmetricKey $publicKey): bool
    {
        $signature = base64_decode($signatureBase64, true);
        if ($signature === false || $signature === '') {
            return false;
        }
        return openssl_verify($data, $signature, $publicKey, OPENSSL_ALGO_SHA256) === 1;
    }

    public static function hmac(string $data, string $key): string
    {
        return base64_encode(hash_hmac('sha256', $data, $key, true));
    }

    public static function hmacValid(string $data, string $key, string $macBase64): bool
    {
        return hash_equals(self::hmac($data, $key), $macBase64);
    }

    public static function uuid4(): string
    {
        $bytes = random_bytes(16);
        $bytes[6] = chr((ord($bytes[6]) & 0x0f) | 0x40);
        $bytes[8] = chr((ord($bytes[8]) & 0x3f) | 0x80);
        return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($bytes), 4));
    }

    /**
     * secret_enc of A.2: base64(RSA-OAEP(SHA-1) encrypted AES-256 key, 384 bytes || nonce, 12 bytes || ciphertext || tag, 16 bytes).
     */
    public static function encryptSecret(string $plaintext, OpenSSLAsymmetricKey $agentPublicKey): string
    {
        $aesKey = random_bytes(32);
        $nonce = random_bytes(12);
        if (!openssl_public_encrypt($aesKey, $encryptedKey, $agentPublicKey, OPENSSL_PKCS1_OAEP_PADDING)) {
            throw new \RuntimeException('Cannot encrypt the secret key: ' . (openssl_error_string() ?: 'unknown OpenSSL error'));
        }
        $ciphertext = openssl_encrypt($plaintext, 'aes-256-gcm', $aesKey, OPENSSL_RAW_DATA, $nonce, $tag, '', 16);
        if ($ciphertext === false) {
            throw new \RuntimeException('Cannot encrypt the secret.');
        }
        return base64_encode($encryptedKey . $nonce . $ciphertext . $tag);
    }
}
