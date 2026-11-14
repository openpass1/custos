<?php

declare(strict_types=1);

namespace Custos\Http;

use Custos\Infra\Cidr;

final class Request
{
    /** @var \Closure(int): ?string|null */
    private ?\Closure $bodyReader;

    /**
     * @param array<string, string> $headers names in lower case
     * @param string $uri the raw request URI with the query string, as the client sent it
     * @param string $basePath the URL path of the site root without a trailing slash ("" or "/custos")
     */
    public function __construct(
        public readonly string $method,
        public readonly string $uri,
        public readonly array $headers = [],
        public readonly string $ip = '127.0.0.1',
        public readonly string $basePath = '',
        public readonly bool $https = false,
        public readonly array $query = [],
        public readonly array $post = [],
        private ?string $body = null,
        ?\Closure $bodyReader = null,
        public readonly array $files = [],
    ) {
        $this->bodyReader = $bodyReader;
    }

    /** @param list<string> $trustedProxies */
    public static function fromGlobals(array $trustedProxies = []): self
    {
        $headers = [];
        foreach ($_SERVER as $name => $value) {
            if (str_starts_with($name, 'HTTP_')) {
                $headers[strtolower(str_replace('_', '-', substr($name, 5)))] = (string) $value;
            }
        }
        foreach (['CONTENT_TYPE' => 'content-type', 'CONTENT_LENGTH' => 'content-length'] as $name => $header) {
            if (isset($_SERVER[$name]) && $_SERVER[$name] !== '') {
                $headers[$header] = (string) $_SERVER[$name];
            }
        }

        $ip = (string) ($_SERVER['REMOTE_ADDR'] ?? '');
        $https = self::serverHttps($_SERVER);
        if ($trustedProxies !== [] && Cidr::allows($ip, $trustedProxies)) {
            // The client is the last address in X-Forwarded-For that is not one of our proxies.
            $chain = array_reverse(array_map('trim', explode(',', $headers['x-forwarded-for'] ?? '')));
            foreach ($chain as $hop) {
                if ($hop !== '' && filter_var($hop, FILTER_VALIDATE_IP) && !Cidr::allows($hop, $trustedProxies)) {
                    $ip = $hop;
                    break;
                }
            }
            $https = strtolower($headers['x-forwarded-proto'] ?? ($https ? 'https' : 'http')) === 'https';
        }

        return new self(
            method: strtoupper((string) ($_SERVER['REQUEST_METHOD'] ?? 'GET')),
            uri: (string) ($_SERVER['REQUEST_URI'] ?? '/'),
            headers: $headers,
            ip: $ip,
            basePath: self::basePath((string) ($_SERVER['SCRIPT_NAME'] ?? '/index.php')),
            https: $https,
            query: $_GET,
            post: $_POST,
            bodyReader: static function (int $limit): ?string {
                $body = file_get_contents('php://input', false, null, 0, $limit + 1);
                return $body === false ? '' : $body;
            },
            files: $_FILES,
        );
    }

    public function header(string $name): ?string
    {
        return $this->headers[strtolower($name)] ?? null;
    }

    /** A page asking for itself again to refresh its live parts (app.js), not a person opening it. */
    public function isLive(): bool
    {
        return $this->method === 'GET' && $this->header('X-Custos-Live') === '1';
    }

    /** URL path relative to the site root, starting with "/". */
    /**
     * Whether the web server says the client came over HTTPS. REQUEST_SCHEME (nginx $scheme, Apache) is the
     * actual scheme and wins: a server block may hard-code HTTPS=on and port 443 for every request, and a
     * secure cookie sent over plain HTTP is dropped by the browser.
     *
     * @param array<string, mixed> $server
     */
    public static function serverHttps(array $server): bool
    {
        $scheme = strtolower((string) ($server['REQUEST_SCHEME'] ?? ''));
        if ($scheme === 'http' || $scheme === 'https') {
            return $scheme === 'https';
        }
        return (!empty($server['HTTPS']) && strtolower((string) $server['HTTPS']) !== 'off')
            || (int) ($server['SERVER_PORT'] ?? 0) === 443;
    }

    /**
     * Where the site starts, from the front controller's SCRIPT_NAME ("/custos/index.php" => "/custos"). Under
     * "php -S" SCRIPT_NAME can be any part of the path with a dot in it ("/updates/agent/1.0.1"): that is not a base.
     */
    public static function basePath(string $scriptName): string
    {
        $script = str_replace('\\', '/', $scriptName);
        return str_ends_with($script, '/index.php') ? rtrim(dirname($script), '/.') : '';
    }

    public function path(): string
    {
        $path = rawurldecode((string) parse_url($this->uri, PHP_URL_PATH));
        if ($this->basePath !== '' && str_starts_with($path, $this->basePath . '/')) {
            $path = substr($path, strlen($this->basePath));
        }
        return $path === '' ? '/' : $path;
    }

    /**
     * The PATH of the request signature (A.2): everything after the first "/api/v1/" in the request URI,
     * query string included. Null when the URI has no "/api/v1/".
     */
    public function apiPath(): ?string
    {
        $pos = strpos($this->uri, '/api/v1/');
        return $pos === false ? null : substr($this->uri, $pos + strlen('/api/v1/'));
    }

    /** The request body, or null when it is longer than $limit bytes. */
    public function body(int $limit = PHP_INT_MAX): ?string
    {
        $length = $this->header('content-length');
        if ($length !== null && ctype_digit($length) && (int) $length > $limit) {
            return null;
        }
        if ($this->body === null) {
            $this->body = $this->bodyReader === null ? '' : ($this->bodyReader)($limit);
        }
        return strlen($this->body) > $limit ? null : $this->body;
    }
}
