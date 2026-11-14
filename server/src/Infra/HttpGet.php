<?php

declare(strict_types=1);

namespace Custos\Infra;

/**
 * HTTP GET with a size limit, for GitHub: the list of releases and release files. Callers take it as a closure
 * (url, byte limit) => [status, body, error], so tests can put a fake GitHub in its place.
 */
final class HttpGet
{
    public function __construct(private readonly string $userAgent)
    {
    }

    /** @return array{0: int, 1: string, 2: string} HTTP status (0 when there was no answer), body, curl error */
    public function __invoke(string $url, int $limit): array
    {
        $body = '';
        $curl = curl_init($url);
        curl_setopt_array($curl, [
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_MAXREDIRS => 5,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_TIMEOUT => 120,
            CURLOPT_HTTPHEADER => ['Accept: application/vnd.github+json', 'User-Agent: ' . $this->userAgent],
            CURLOPT_WRITEFUNCTION => function ($curl, string $chunk) use (&$body, $limit): int {
                if (strlen($body) + strlen($chunk) > $limit) {
                    return 0;
                }
                $body .= $chunk;
                return strlen($chunk);
            },
        ]);
        $ok = curl_exec($curl);
        $status = (int) curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
        $error = $ok === false ? curl_error($curl) : '';
        return [$ok === false ? 0 : $status, $body, $error];
    }
}
