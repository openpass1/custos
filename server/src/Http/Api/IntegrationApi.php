<?php

declare(strict_types=1);

namespace Custos\Http\Api;

use Custos\App;
use Custos\Domain\Integrations;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Http\Web\Controller\DashboardController;
use Custos\Infra\Cidr;
use Custos\Infra\Log;

/**
 * Endpoints for monitoring systems (section 10):
 *   GET /api/health                     no token: database and cron state, 503 when either is bad
 *   GET /api/zabbix/discovery/agents    low-level discovery of machines
 *   GET /api/zabbix/discovery/tasks     low-level discovery of tasks
 *   GET /api/zabbix/status              every status in one answer
 * The Zabbix endpoints need the token (?token= or X-Custos-Token) and an address from the integrations allow-list.
 */
final class IntegrationApi
{
    public function __construct(private readonly App $app)
    {
    }

    public static function handles(string $path): bool
    {
        return $path === '/api/health' || str_starts_with($path, '/api/zabbix/');
    }

    public function handle(Request $request): Response
    {
        try {
            $response = $this->dispatch($request);
        } catch (\Throwable $e) {
            Log::error("Integration {$request->method} {$request->uri}: $e");
            $response = Response::json(500, ['error' => 'internal']);
        }
        $response->headers += ['X-Content-Type-Options' => 'nosniff', 'Cache-Control' => 'no-store'];
        return $response;
    }

    private function dispatch(Request $request): Response
    {
        if ($request->method !== 'GET' && $request->method !== 'HEAD') {
            return Response::json(405, ['error' => 'method']);
        }
        $path = $request->path();
        if ($path === '/api/health') {
            [$status, $body] = Integrations::health(fn() => $this->app->db(), time(), DashboardController::CRON_LATE_S);
            return Response::json($status, $body);
        }

        $settings = $this->app->settings();
        if (!Cidr::allows($request->ip, Cidr::fromStored($settings->get('ip_allow_integrations')))) {
            return Response::json(403, ['error' => 'ip_not_allowed', 'ip' => $request->ip]);
        }
        $token = (string) ($request->headers['x-custos-token'] ?? '');
        if ($token === '' && is_string($request->query['token'] ?? null)) {
            $token = $request->query['token'];
        }
        $integrations = $this->app->integrations();
        if (!$integrations->tokenMatches('zabbix_token', $token)) {
            return Response::json(401, ['error' => 'token']);
        }
        $board = $this->app->board();
        return match ($path) {
            '/api/zabbix/discovery/agents' => Response::json(200, Integrations::discoverAgents($board)),
            '/api/zabbix/discovery/tasks' => Response::json(200, Integrations::discoverTasks($board)),
            '/api/zabbix/status' => Response::json(200, $integrations->status($board)),
            default => Response::json(404, ['error' => 'not_found']),
        };
    }
}
