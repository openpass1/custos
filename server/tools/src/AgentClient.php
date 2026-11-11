<?php

declare(strict_types=1);

namespace Custos\Tools;

use OpenSSLAsymmetricKey;

/**
 * The agent side of the protocol: request signatures, response checks and registration (A.2, A.3).
 *
 * It is written from docs/protocol.md on purpose without using the server code, so that it checks the
 * server instead of repeating its mistakes. The Windows client follows the same rules.
 */
final class AgentClient
{
    public const PROTOCOL = '1';

    /** @var callable(string, string, array<string, string>, string): array{0: int, 1: array<string, string>, 2: string, 3?: string} */
    private $transport;

    /**
     * @param string $serverUrl without a trailing slash
     * @param ?string $serverPublicKey the pinned server key (PEM), null before the first registration
     * @param ?callable $transport replaces HTTP in tests: fn(method, url, headers, body) => [status, headers, body]
     */
    public function __construct(
        private readonly string $serverUrl,
        private readonly OpenSSLAsymmetricKey $privateKey,
        public ?string $agentId = null,
        public ?string $serverPublicKey = null,
        ?callable $transport = null
    ) {
        $this->transport = $transport ?? self::curl(...);
    }

    /** A signed request to {server_url}/api/v1/{path}; the answer is checked against the pinned server key. */
    public function call(string $method, string $path, ?array $body = null): ApiResult
    {
        $json = $body === null ? '' : self::json($body);
        $nonce = bin2hex(random_bytes(16));
        $headers = $this->headers($method, $path, $json, $nonce);
        $headers['X-Custos-Agent'] = (string) $this->agentId;
        return $this->send($method, $path, $headers, $json, $nonce, $this->serverPublicKey);
    }

    /**
     * Registration (A.3). On success sets agentId and pins the server key.
     *
     * @param string $code the registration code "KEY" or "KEY#FINGERPRINT"
     * @param array{hostname: string, machine_guid: string, os: string, agent_version: string, time_zone: string} $info
     * @return array{ok: bool, status: ?string, reason: string, result: ApiResult}
     */
    public function register(string $code, array $info): array
    {
        [$key, $fingerprint] = self::parseCode($code);
        $json = self::json($info + ['public_key' => self::publicPem($this->privateKey)]);
        $nonce = bin2hex(random_bytes(16));
        $headers = $this->headers('POST', 'agent/register', $json, $nonce);
        $headers['X-Custos-Registration-Mac'] = base64_encode(hash_hmac('sha256', self::requestString('POST', 'agent/register', $headers['X-Custos-Time'], $nonce, $json), $key, true));

        [$status, $responseHeaders, $responseBody, $error] = $this->transport('POST', 'agent/register', $headers, $json);
        $responseString = self::responseString($status, $responseHeaders['x-custos-time'] ?? '', $nonce, $responseBody);
        $macValid = isset($responseHeaders['x-custos-registration-mac'])
            && hash_equals(base64_encode(hash_hmac('sha256', $responseString, $key, true)), $responseHeaders['x-custos-registration-mac']);
        $data = json_decode($responseBody, true) ?: [];

        if ($status !== 200) {
            // Without a pinned key the agent can check an error only by the registration HMAC.
            $signed = $macValid && ($this->serverPublicKey === null
                || self::verify($responseString, $responseHeaders['x-custos-signature'] ?? '', $this->serverPublicKey));
            $result = new ApiResult($status, $responseBody, $signed, $responseHeaders, $error);
            $reason = $status === 403 && ($data['error'] ?? '') === 'bad_registration_key'
                ? 'the registration key was not accepted'
                : $result->describe();
            return ['ok' => false, 'status' => null, 'reason' => $reason, 'result' => $result];
        }

        $serverKey = (string) ($data['server_public_key'] ?? '');
        $checks = [
            'registration HMAC of the answer' => $macValid,
            'server signature of the answer' => $serverKey !== '' && self::verify($responseString, $responseHeaders['x-custos-signature'] ?? '', $serverKey),
            'server key fingerprint from the registration code' => $fingerprint === null
                || ($serverKey !== '' && self::normalizeFingerprint(self::fingerprint($serverKey)) === self::normalizeFingerprint($fingerprint)),
            'server key pinned earlier' => $this->serverPublicKey === null || self::sameKey($this->serverPublicKey, $serverKey),
        ];
        $result = new ApiResult($status, $responseBody, $checks['server signature of the answer'], $responseHeaders);
        foreach ($checks as $name => $passed) {
            if (!$passed) {
                return ['ok' => false, 'status' => null, 'reason' => "answer rejected: $name does not match", 'result' => $result];
            }
        }
        $this->agentId = (string) $data['agent_id'];
        $this->serverPublicKey = $serverKey;
        return ['ok' => true, 'status' => (string) $data['status'], 'reason' => '', 'result' => $result];
    }

    /** Decrypts secret_enc (A.2): RSA-OAEP(SHA-1) key, 384 bytes || nonce, 12 || ciphertext || tag, 16; AES-256-GCM. */
    public function decryptSecret(string $secretEnc): ?string
    {
        $raw = base64_decode($secretEnc, true);
        if ($raw === false || strlen($raw) < 384 + 12 + 16) {
            return null;
        }
        if (!openssl_private_decrypt(substr($raw, 0, 384), $aesKey, $this->privateKey, OPENSSL_PKCS1_OAEP_PADDING)) {
            return null;
        }
        $plain = openssl_decrypt(substr($raw, 396, -16), 'aes-256-gcm', $aesKey, OPENSSL_RAW_DATA, substr($raw, 384, 12), substr($raw, -16));
        return $plain === false ? null : $plain;
    }

    /** Checks the signature of the server's update command (A.10): "{version}\n{sha256}". */
    public function verifyUpdate(string $version, string $sha256, string $signature): bool
    {
        return $this->serverPublicKey !== null && self::verify("$version\n$sha256", $signature, $this->serverPublicKey);
    }

    /** Checks the signature of the server's 7-Zip command (A.10): "7zip\n{version}\n{sha256}". */
    public function verifySevenZip(string $version, string $sha256, string $signature): bool
    {
        return $this->serverPublicKey !== null && self::verify("7zip\n$version\n$sha256", $signature, $this->serverPublicKey);
    }

    /**
     * Downloads a package (A.10): a signed GET whose 200 body is the file itself and is not signed; it is checked by
     * the SHA-256 and signature from the command instead. An error answer is signed like any other.
     *
     * @return array{ok: bool, body: string, reason: string}
     */
    public function download(string $path): array
    {
        $result = $this->call('GET', $path);
        if ($result->status === 200) {
            return ['ok' => true, 'body' => $result->body, 'reason' => ''];
        }
        return ['ok' => false, 'body' => '', 'reason' => $result->describe()];
    }

    public static function requestString(string $method, string $path, string $time, string $nonce, string $body): string
    {
        return "$method\n$path\n$time\n$nonce\n" . hash('sha256', $body);
    }

    public static function responseString(int $status, string $time, string $nonce, string $body): string
    {
        return sprintf('%03d', $status) . "\n$time\n$nonce\n" . hash('sha256', $body);
    }

    /** First 16 hex characters of SHA-256 of the public key DER, in groups of four. */
    public static function fingerprint(string $publicPem): string
    {
        $der = base64_decode(preg_replace('/-----[^-]+-----|\s+/', '', $publicPem), true) ?: '';
        return implode('-', str_split(strtoupper(substr(hash('sha256', $der), 0, 16)), 4));
    }

    /** @return array{0: string, 1: ?string} the normalized key and the fingerprint part, if any */
    public static function parseCode(string $code): array
    {
        [$key, $fingerprint] = array_pad(explode('#', trim($code), 2), 2, null);
        $fingerprint = $fingerprint === null || trim($fingerprint) === '' ? null : trim($fingerprint);
        return [strtoupper(str_replace([' ', '-'], '', (string) $key)), $fingerprint];
    }

    public static function publicPem(OpenSSLAsymmetricKey $key): string
    {
        return openssl_pkey_get_details($key)['key'];
    }

    private function headers(string $method, string $path, string $body, string $nonce): array
    {
        $time = (string) time();
        openssl_sign(self::requestString($method, $path, $time, $nonce, $body), $signature, $this->privateKey, OPENSSL_ALGO_SHA256);
        return [
            'Content-Type' => 'application/json',
            'X-Custos-Protocol' => self::PROTOCOL,
            'X-Custos-Time' => $time,
            'X-Custos-Nonce' => $nonce,
            'X-Custos-Signature' => base64_encode($signature),
        ];
    }

    private function send(string $method, string $path, array $headers, string $body, string $nonce, ?string $serverKey): ApiResult
    {
        [$status, $responseHeaders, $responseBody, $error] = $this->transport($method, $path, $headers, $body);
        $signed = $serverKey !== null && $status !== 0 && self::verify(
            self::responseString($status, $responseHeaders['x-custos-time'] ?? '', $nonce, $responseBody),
            $responseHeaders['x-custos-signature'] ?? '',
            $serverKey
        );
        return new ApiResult($status, $responseBody, $signed, $responseHeaders, $error);
    }

    /** @return array{0: int, 1: array<string, string>, 2: string, 3: string} */
    private function transport(string $method, string $path, array $headers, string $body): array
    {
        $result = ($this->transport)($method, $this->serverUrl . '/api/v1/' . $path, $headers, $body);
        return [$result[0], $result[1], $result[2], $result[3] ?? ''];
    }

    private static function verify(string $data, string $signature, string $publicPem): bool
    {
        $raw = base64_decode($signature, true);
        return $raw !== false && $raw !== '' && openssl_verify($data, $raw, $publicPem, OPENSSL_ALGO_SHA256) === 1;
    }

    private static function sameKey(string $a, string $b): bool
    {
        $ka = @openssl_pkey_get_public($a);
        $kb = @openssl_pkey_get_public($b);
        return $ka !== false && $kb !== false && openssl_pkey_get_details($ka)['key'] === openssl_pkey_get_details($kb)['key'];
    }

    private static function normalizeFingerprint(string $fingerprint): string
    {
        return strtoupper(str_replace(['-', ' '], '', $fingerprint));
    }

    private static function json(array $data): string
    {
        return json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
    }

    /** HTTP through curl. Compressed answers are unpacked: the signature covers the unpacked body (A.2). */
    private static function curl(string $method, string $url, array $headers, string $body): array
    {
        $responseHeaders = [];
        $curl = curl_init($url);
        $lines = [];
        foreach ($headers as $name => $value) {
            $lines[] = "$name: $value";
        }
        curl_setopt_array($curl, [
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_POSTFIELDS => $body,
            CURLOPT_HTTPHEADER => $lines,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_ENCODING => '',
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_TIMEOUT => 120,
            CURLOPT_HEADERFUNCTION => function ($curl, string $line) use (&$responseHeaders): int {
                if (str_contains($line, ':')) {
                    [$name, $value] = explode(':', $line, 2);
                    $responseHeaders[strtolower(trim($name))] = trim($value);
                }
                return strlen($line);
            },
        ]);
        $responseBody = curl_exec($curl);
        if ($responseBody === false) {
            return [0, [], '', curl_error($curl)];
        }
        return [(int) curl_getinfo($curl, CURLINFO_RESPONSE_CODE), $responseHeaders, (string) $responseBody, ''];
    }
}
