<?php

declare(strict_types=1);

namespace Custos\Http\Web;

use Custos\App;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Http\Web\Controller\AgentsController;
use Custos\Http\Web\Controller\AuditController;
use Custos\Http\Web\Controller\AuthController;
use Custos\Http\Web\Controller\RunsController;
use Custos\Http\Web\Controller\SettingsController;
use Custos\Http\Web\Controller\UsersController;
use Custos\Infra\Cidr;
use Custos\Infra\Log;

/**
 * The web site: IP allow-list, session, CSRF check, routing to controllers.
 */
final class Web
{
    private const ID = '([0-9a-fA-F-]{36})';

    /** [method, path pattern, controller, action, public] */
    private const ROUTES = [
        ['GET', '#^/$#', AgentsController::class, 'home', false],
        ['GET', '#^/login$#', AuthController::class, 'form', true],
        ['POST', '#^/login$#', AuthController::class, 'login', true],
        ['POST', '#^/logout$#', AuthController::class, 'logout', false],
        ['GET', '#^/agents$#', AgentsController::class, 'index', false],
        ['GET', '#^/agents/' . self::ID . '$#', AgentsController::class, 'show', false],
        ['POST', '#^/agents/' . self::ID . '/(approve|replace|disable|enable|delete|rename)$#', AgentsController::class, 'action', false],
        ['GET', '#^/runs$#', RunsController::class, 'index', false],
        ['GET', '#^/runs/' . self::ID . '$#', RunsController::class, 'show', false],
        ['GET', '#^/runs/' . self::ID . '/log$#', RunsController::class, 'log', false],
        ['GET', '#^/runs/' . self::ID . '/log/download$#', RunsController::class, 'download', false],
        ['GET', '#^/users$#', UsersController::class, 'index', false],
        ['POST', '#^/users$#', UsersController::class, 'create', false],
        ['POST', '#^/users/(\d+)/(password|disable|enable)$#', UsersController::class, 'action', false],
        ['GET', '#^/audit$#', AuditController::class, 'index', false],
        ['GET', '#^/settings$#', SettingsController::class, 'index', false],
        ['POST', '#^/settings$#', SettingsController::class, 'save', false],
        ['POST', '#^/settings/registration-key$#', SettingsController::class, 'regenerateKey', false],
    ];

    public function __construct(private readonly App $app)
    {
    }

    public function handle(Request $request): Response
    {
        try {
            $response = $this->dispatch($request);
        } catch (\Throwable $e) {
            Log::error("Web {$request->method} {$request->uri}: $e");
            $response = Response::text($this->app->lang()->get('error.internal'), 500);
        }
        $response->headers += [
            'X-Frame-Options' => 'DENY',
            'X-Content-Type-Options' => 'nosniff',
            'Referrer-Policy' => 'same-origin',
            'Content-Security-Policy' => "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; form-action 'self'",
            'Cache-Control' => 'no-store',
        ];
        return $response;
    }

    private function dispatch(Request $request): Response
    {
        $lang = $this->app->lang();
        if (!Cidr::allows($request->ip, Cidr::fromStored($this->app->settings()->get('ip_allow_site')))) {
            return Response::text($lang->get('error.ip_not_allowed', ['ip' => $request->ip]), 403);
        }

        $path = $request->path();
        $route = null;
        $methodMismatch = false;
        foreach (self::ROUTES as [$method, $pattern, $class, $action, $public]) {
            if (preg_match($pattern, $path, $m)) {
                if ($method === $request->method || ($method === 'GET' && $request->method === 'HEAD')) {
                    $route = [$class, $action, $public, array_slice($m, 1)];
                    break;
                }
                $methodMismatch = true;
            }
        }
        if ($route === null) {
            return Response::text($lang->get($methodMismatch ? 'error.method' : 'error.not_found'), $methodMismatch ? 405 : 404);
        }
        [$class, $action, $public, $params] = $route;

        $session = Session::start($this->app, $request);
        if ($request->method === 'POST' && !$session->checkCsrf((string) ($request->post['_csrf'] ?? ''))) {
            return Response::text($lang->get('error.csrf'), 400);
        }
        if (!$public && $session->user() === null) {
            return Response::redirect($request->basePath . '/login');
        }

        $view = new View($this->app, $request, $session);
        /** @var Controller $controller */
        $controller = new $class($this->app, $request, $session, $view);
        return $controller->$action(...$params);
    }
}
