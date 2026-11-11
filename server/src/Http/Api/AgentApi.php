<?php

declare(strict_types=1);

namespace Custos\Http\Api;

use Custos\App;
use Custos\Domain\Agents;
use Custos\Domain\Input;
use Custos\Domain\InvalidInput;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Infra\Cidr;
use Custos\Infra\Crypto;
use Custos\Infra\Log;
use Custos\Infra\Time;

/**
 * The agent API under /api/v1/ (A.2–A.8).
 *
 * Every answer goes out through sign(): the signature is added in this one place, after the status and body
 * are known, so errors are signed the same way as successful answers.
 */
final class AgentApi
{
    public const PROTOCOL = '1';
    public const MAX_BODY_BYTES = 8 * 1024 * 1024;
    public const MAX_CLOCK_SKEW_S = 300;

    /** [method, path pattern, handler]; patterns match the API path without the query string. */
    private const ROUTES = [
        ['POST', '#^agent/register$#', 'register'],
        ['POST', '#^agent/heartbeat$#', 'heartbeat'],
        ['POST', '#^agent/config$#', 'config'],
        ['POST', '#^agent/runs/start$#', 'runStart'],
        ['POST', '#^agent/runs/progress$#', 'runProgress'],
        ['POST', '#^agent/runs/finish$#', 'runFinish'],
        ['POST', '#^agent/runs/([^/]+)/log$#', 'runLog'],
        ['POST', '#^agent/commands/([^/]+)/result$#', 'commandResult'],
        ['POST', '#^agent/inventory$#', 'inventory'],
        ['GET', '#^agent/updates/([^/]+)$#', 'downloadUpdate'],
        ['GET', '#^agent/sevenzip/([^/]+)$#', 'downloadSevenZip'],
    ];

    public function __construct(private readonly App $app)
    {
    }

    public function handle(Request $request): Response
    {
        $path = (string) $request->apiPath();
        // The nonce goes into the response signature as the agent sent it; garbage is replaced with "".
        $nonce = (string) $request->header('x-custos-nonce');
        if (!preg_match('/^[\x21-\x7e]{1,128}$/', $nonce)) {
            $nonce = '';
        }
        $macKey = null;
        try {
            $response = $this->dispatch($request, $path, $macKey);
        } catch (ApiError $e) {
            // Waiting for approval and a disabled agent are normal states, not worth a log line per request.
            if (!in_array($e->error, ['pending', 'disabled', 'clock_skew'], true)) {
                Log::warning(sprintf('API %s %s from %s agent %s: %d %s %s', $request->method, $path, $request->ip,
                    $request->header('x-custos-agent') ?? '-', $e->status, $e->error, $e->getMessage()));
            }
            $response = $e->response();
        } catch (\Throwable $e) {
            Log::error("API {$request->method} $path: $e");
            $response = (new ApiError(500, 'internal_error', 'Internal server error'))->response();
        }
        return $this->sign($response, $nonce, $macKey);
    }

    /**
     * Signs "{STATUS}\n{TIME}\n{NONCE}\n{sha256hex(body)}" with the server key (A.2), and with the registration
     * key as well when $macKey is set (A.3).
     */
    private function sign(Response $response, string $nonce, ?string $macKey): Response
    {
        if ($response->file !== null) {
            // A package body is checked by its SHA-256 and the signature in the command, not by the answer (A.2, A.10).
            $response->headers['Cache-Control'] = 'no-store';
            return $response;
        }
        $time = (string) time();
        $string = $response->status . "\n" . $time . "\n" . $nonce . "\n" . hash('sha256', $response->body);
        $response->headers['X-Custos-Time'] = $time;
        $response->headers['X-Custos-Signature'] = Crypto::sign($string, $this->app->keys()->privateKey());
        if ($macKey !== null) {
            $response->headers['X-Custos-Registration-Mac'] = Crypto::hmac($string, $macKey);
        }
        $response->headers['Cache-Control'] = 'no-store';
        return $response;
    }

    private function dispatch(Request $request, string $path, ?string &$macKey): Response
    {
        [$handler, $params] = $this->route($request->method, $path);
        $settings = $this->app->settings();
        if ($handler === 'register' && $settings->get('registration_key') !== '') {
            // Registration errors carry the registration HMAC too, except bad_registration_key.
            $macKey = $settings->get('registration_key');
        }

        if ($request->header('x-custos-protocol') !== self::PROTOCOL) {
            throw ApiError::badRequest('Unsupported protocol: X-Custos-Protocol must be ' . self::PROTOCOL);
        }
        if (!Cidr::allows($request->ip, Cidr::fromStored($settings->get('ip_allow_agents')))) {
            throw new ApiError(403, 'forbidden', "Address {$request->ip} is not in the networks allowed for agents");
        }
        $body = $request->body(self::MAX_BODY_BYTES);
        if ($body === null) {
            throw new ApiError(413, 'too_large', 'Request body is larger than 8 MB');
        }
        $nonce = (string) $request->header('x-custos-nonce');
        if (!preg_match('/^[0-9a-fA-F]{32}$/', $nonce)) {
            throw ApiError::badRequest('X-Custos-Nonce must be 32 hex characters');
        }
        $time = (string) $request->header('x-custos-time');
        $signature = (string) $request->header('x-custos-signature');
        $signed = $request->method . "\n" . $path . "\n" . $time . "\n" . $nonce . "\n" . hash('sha256', $body);

        if ($handler === 'register') {
            $this->checkTime($time);
            return $this->register($request, $body, $signed, $signature, $nonce, $macKey);
        }

        $agentId = strtolower((string) $request->header('x-custos-agent'));
        if ($agentId === '') {
            throw ApiError::badRequest('X-Custos-Agent is required');
        }
        $agent = $this->app->agents()->find($agentId);
        if ($agent === null) {
            throw new ApiError(404, 'unknown_agent', 'Agent is not registered on this server');
        }
        $this->checkTime($time);
        $key = Crypto::loadPublicKey($agent['public_key']);
        if ($key === null || !Crypto::verify($signed, $signature, $key)) {
            throw new ApiError(401, 'bad_signature', 'Request signature is not valid');
        }
        $this->useNonce($agent['id'], $nonce);

        if ($handler !== 'heartbeat' && $agent['status'] !== 'approved') {
            throw new ApiError(403, $agent['status'], $agent['status'] === 'pending'
                ? 'Agent is waiting for approval on the server'
                : 'Agent is disabled on the server');
        }

        try {
            $input = $request->method === 'GET' ? new Input([]) : Input::fromJson($body);
            return match ($handler) {
                'heartbeat' => $this->heartbeat($agent, $input, $request),
                'config' => $this->ok(fn() => $this->app->configSnapshots()->apply($agent['id'], $input, $body)),
                'runStart' => $this->ok(fn() => $this->app->reports()->start($agent['id'], $input)),
                'runProgress' => $this->ok(fn() => $this->app->reports()->progress($agent['id'], $input)),
                'runFinish' => $this->ok(fn() => $this->app->reports()->finish($agent['id'], $input)),
                'runLog' => $this->ok(fn() => $this->app->reports()->log($agent['id'], $params[0], $input)),
                'commandResult' => $this->ok(fn() => $this->app->commands()->result($agent['id'], $params[0], $input)),
                'inventory' => $this->ok(fn() => $this->app->reports()->inventory($agent['id'], $input)),
                'downloadUpdate' => $this->package('agent', $params[0]),
                'downloadSevenZip' => $this->package('7zip', $params[0]),
            };
        } catch (InvalidInput $e) {
            throw ApiError::badRequest($e->getMessage());
        }
    }

    /** @return array{0: string, 1: list<string>} */
    private function route(string $method, string $path): array
    {
        $route = (string) strtok($path, '?');
        foreach (self::ROUTES as [$routeMethod, $pattern, $handler]) {
            if (preg_match($pattern, $route, $m)) {
                if ($method !== $routeMethod) {
                    throw ApiError::badRequest("Method $method is not allowed for $route");
                }
                return [$handler, array_slice($m, 1)];
            }
        }
        throw ApiError::badRequest("Unknown endpoint $route");
    }

    private function checkTime(string $time): void
    {
        if (!preg_match('/^\d{1,12}$/', $time)) {
            throw ApiError::badRequest('X-Custos-Time must be unix time in seconds');
        }
        if (abs(time() - (int) $time) > self::MAX_CLOCK_SKEW_S) {
            throw new ApiError(401, 'clock_skew', 'Agent clock differs from the server clock by more than 5 minutes',
                ['server_time' => Time::isoNow()]);
        }
    }

    /** Remembers the nonce; a nonce seen before within 10 minutes means a replayed request. */
    private function useNonce(string $scope, string $nonce): void
    {
        $inserted = $this->app->db()->exec(
            'INSERT IGNORE INTO nonces (scope, nonce, created_at) VALUES (?, ?, ?)',
            [$scope, strtolower($nonce), Time::now()]
        );
        if ($inserted === 0) {
            throw new ApiError(401, 'bad_signature', 'Nonce has already been used');
        }
    }

    /** A.3: the registration key HMAC first, then the agent key signature. */
    private function register(Request $request, string $body, string $signed, string $signature, string $nonce, ?string &$macKey): Response
    {
        $key = $this->app->settings()->get('registration_key');
        if ($key === '' || !Crypto::hmacValid($signed, $key, (string) $request->header('x-custos-registration-mac'))) {
            $macKey = null;
            throw new ApiError(403, 'bad_registration_key', 'Registration key does not match');
        }
        try {
            $in = Input::fromJson($body);
            $info = [
                'hostname' => $in->string('hostname'),
                'machine_guid' => $in->string('machine_guid', 64),
                'os' => $in->string('os'),
                'agent_version' => $in->string('agent_version', 32),
                'time_zone' => self::timeZone($in),
            ];
            $publicKey = Crypto::loadPublicKey($in->string('public_key', 16384));
        } catch (InvalidInput $e) {
            throw ApiError::badRequest($e->getMessage());
        }
        if ($publicKey === null) {
            throw ApiError::badRequest('public_key is not an RSA public key in PEM format');
        }
        if (Crypto::keyBits($publicKey) < Agents::MIN_KEY_BITS) {
            throw ApiError::badRequest('public_key must be at least ' . Agents::MIN_KEY_BITS . ' bits');
        }
        if (!Crypto::verify($signed, $signature, $publicKey)) {
            throw new ApiError(401, 'bad_signature', 'Request signature does not match public_key');
        }
        $this->useNonce('register', $nonce);

        $agent = $this->app->agents()->register($info, $publicKey, $request->ip);
        return Response::json(200, [
            'agent_id' => $agent['id'],
            'status' => $agent['status'],
            'server_public_key' => $this->app->keys()->publicPem(),
            'server_name' => $this->app->settings()->get('server_name'),
        ]);
    }

    private function heartbeat(array $agent, Input $in, Request $request): Response
    {
        $running = $in->object('running', true);
        $hb = [
            'agent_version' => $in->string('agent_version', 32),
            'config_revision' => $in->int('config_revision', true),
            'time_zone' => self::timeZone($in),
            'service_started_at' => $in->time('service_started_at', true),
            'state' => $in->enum('state', ['idle', 'running', 'paused']),
            'running' => $running === null ? null : [
                'run_id' => $running->uuid('run_id'),
                'task_id' => $running->uuid('task_id'),
                'paused_at' => Time::iso($running->time('paused_at', true)),
            ],
            'queue' => $in->uuids('queue'),
            'sevenzip_version' => $in->string('sevenzip_version', 32, true),
        ];
        $this->app->agents()->heartbeat($agent['id'], $hb, $request->ip, $agent['status']);

        $approved = $agent['status'] === 'approved';
        $wantConfig = $approved
            && ($agent['config_revision'] === null || (int) $agent['config_revision'] !== $hb['config_revision']);
        return Response::json(200, [
            'status' => $agent['status'],
            'server_time' => Time::isoNow(),
            'heartbeat_interval_s' => $this->app->settings()->int('heartbeat_interval_s'),
            'want_config' => $wantConfig,
            'commands' => $approved ? $this->app->commands()->deliver($agent['id']) : [],
        ]);
    }

    /** GET agent/updates/{version} and agent/sevenzip/{version}: the package itself, not signed (A.10). */
    private function package(string $kind, string $version): Response
    {
        $file = preg_match('/^[0-9A-Za-z.+-]{1,32}$/', $version) ? $this->app->packages()->file($kind, $version) : null;
        if ($file === null) {
            // Not 404: to an agent 404 means only unknown_agent (A.11).
            throw ApiError::badRequest("No package $version on this server");
        }
        return Response::file($file, $kind === '7zip' ? 'application/vnd.microsoft.portable-executable' : 'application/zip', basename($file));
    }

    private function ok(callable $action): Response
    {
        $action();
        return Response::json(200, ['ok' => true]);
    }

    private static function timeZone(Input $in): string
    {
        $zone = $in->string('time_zone', 64);
        try {
            new \DateTimeZone($zone);
        } catch (\Exception) {
            throw new InvalidInput('time_zone must be an IANA time zone such as Europe/Berlin');
        }
        return $zone;
    }
}
