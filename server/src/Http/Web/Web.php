<?php

declare(strict_types=1);

namespace Custos\Http\Web;

use Custos\App;
use Custos\Http\Request;
use Custos\Http\Response;
use Custos\Http\Web\Controller\AgentsController;
use Custos\Http\Web\Controller\AuditController;
use Custos\Http\Web\Controller\AuthController;
use Custos\Http\Web\Controller\DashboardController;
use Custos\Http\Web\Controller\GroupsController;
use Custos\Http\Web\Controller\IntegrationsController;
use Custos\Http\Web\Controller\NoticesController;
use Custos\Http\Web\Controller\NotificationsController;
use Custos\Http\Web\Controller\ProblemsController;
use Custos\Http\Web\Controller\ProfileController;
use Custos\Http\Web\Controller\RunsController;
use Custos\Http\Web\Controller\SearchController;
use Custos\Http\Web\Controller\SettingsController;
use Custos\Http\Web\Controller\TasksController;
use Custos\Http\Web\Controller\TemplatesController;
use Custos\Http\Web\Controller\UpdatesController;
use Custos\Http\Web\Controller\UsersController;
use Custos\Http\Web\Controller\WallController;
use Custos\Infra\Cidr;
use Custos\Infra\Log;

/**
 * The web site: IP allow-list, session, CSRF check, routing to controllers.
 */
final class Web
{
    private const ID = '([0-9a-fA-F-]{36})';

    /**
     * [method, path pattern, controller, action, access]: access is "public" (no login), "user", "live" or "token".
     * "live" pages are polled by the browser for fresh data: they need a login but do not count as activity, so the
     * inactivity timeout still works with a dashboard left open, and they answer 401 instead of redirecting. A "user"
     * page asked for again by itself (header X-Custos-Live, app.js) is handled the same way.
     * "token" pages (the wall screen, 7.9) need no login and no session: the wall token and the integrations allow-list
     * take the place of both, and the site allow-list does not apply.
     */
    private const ROUTES = [
        ['GET', '#^/$#', DashboardController::class, 'index', 'user'],
        ['GET', '#^/dashboard/live$#', DashboardController::class, 'live', 'live'],
        ['GET', '#^/login$#', AuthController::class, 'form', 'public'],
        ['POST', '#^/login$#', AuthController::class, 'login', 'public'],
        ['POST', '#^/logout$#', AuthController::class, 'logout', 'user'],
        ['GET', '#^/agents$#', AgentsController::class, 'index', 'user'],
        ['GET', '#^/tasks$#', TasksController::class, 'index', 'user'],
        ['GET', '#^/search$#', SearchController::class, 'index', 'user'],
        ['GET', '#^/search/suggest$#', SearchController::class, 'suggest', 'live'],
        ['GET', '#^/agents/' . self::ID . '$#', AgentsController::class, 'show', 'user'],
        ['POST', '#^/agents/' . self::ID . '/(approve|replace|disable|enable|delete|rename|group|inventory|update|sevenzip)$#', AgentsController::class, 'action', 'user'],
        ['POST', '#^/agents/' . self::ID . '/commands/' . self::ID . '/cancel$#', AgentsController::class, 'cancel', 'user'],
        ['GET', '#^/agents/' . self::ID . '/settings$#', AgentsController::class, 'settings', 'user'],
        ['POST', '#^/agents/' . self::ID . '/settings$#', AgentsController::class, 'saveSettings', 'user'],
        ['GET', '#^/agents/' . self::ID . '/tasks/new$#', TasksController::class, 'create', 'user'],
        ['POST', '#^/agents/' . self::ID . '/tasks/new$#', TasksController::class, 'store', 'user'],
        ['GET', '#^/agents/' . self::ID . '/tasks/' . self::ID . '$#', TasksController::class, 'show', 'user'],
        ['POST', '#^/agents/' . self::ID . '/tasks/' . self::ID . '/(run|stop|pause|resume|enable|disable|delete)$#', TasksController::class, 'command', 'user'],
        ['GET', '#^/agents/' . self::ID . '/tasks/' . self::ID . '/edit$#', TasksController::class, 'edit', 'user'],
        ['POST', '#^/agents/' . self::ID . '/tasks/' . self::ID . '/edit$#', TasksController::class, 'save', 'user'],
        ['GET', '#^/agents/' . self::ID . '/tasks/' . self::ID . '/versions$#', TasksController::class, 'versions', 'user'],
        ['POST', '#^/agents/' . self::ID . '/tasks/' . self::ID . '/versions/(\d{1,18})/restore$#', TasksController::class, 'restore', 'user'],
        ['GET', '#^/templates$#', TemplatesController::class, 'index', 'user'],
        ['GET', '#^/templates/new$#', TemplatesController::class, 'create', 'user'],
        ['POST', '#^/templates/new$#', TemplatesController::class, 'store', 'user'],
        ['GET', '#^/templates/(\d{1,10})$#', TemplatesController::class, 'show', 'user'],
        ['GET', '#^/templates/(\d{1,10})/edit$#', TemplatesController::class, 'edit', 'user'],
        ['POST', '#^/templates/(\d{1,10})/edit$#', TemplatesController::class, 'update', 'user'],
        ['POST', '#^/templates/(\d{1,10})/delete$#', TemplatesController::class, 'delete', 'user'],
        ['POST', '#^/templates/(\d{1,10})/apply$#', TemplatesController::class, 'apply', 'user'],
        ['GET', '#^/updates$#', UpdatesController::class, 'index', 'user'],
        ['POST', '#^/updates/upload$#', UpdatesController::class, 'upload', 'user'],
        ['POST', '#^/updates/install$#', UpdatesController::class, 'install', 'user'],
        ['POST', '#^/updates/sevenzip/check$#', UpdatesController::class, 'sevenZipCheck', 'user'],
        ['POST', '#^/updates/sevenzip/download$#', UpdatesController::class, 'sevenZipDownload', 'user'],
        ['POST', '#^/updates/sevenzip/upload$#', UpdatesController::class, 'sevenZipUpload', 'user'],
        ['POST', '#^/updates/sevenzip/settings$#', UpdatesController::class, 'sevenZipSettings', 'user'],
        ['POST', '#^/updates/releases/check$#', UpdatesController::class, 'releasesCheck', 'user'],
        ['POST', '#^/updates/releases/settings$#', UpdatesController::class, 'releasesSettings', 'user'],
        ['POST', '#^/updates/releases/client$#', UpdatesController::class, 'clientDownload', 'user'],
        ['POST', '#^/updates/site/install$#', UpdatesController::class, 'siteInstall', 'user'],
        ['POST', '#^/updates/site/upload$#', UpdatesController::class, 'siteUpload', 'user'],
        ['POST', '#^/updates/site/cancel$#', UpdatesController::class, 'siteCancel', 'user'],
        ['POST', '#^/updates/agent/([0-9A-Za-z.+-]{1,32})/current$#', UpdatesController::class, 'current', 'user'],
        ['POST', '#^/updates/(agent|7zip)/([0-9A-Za-z.+-]{1,32})/delete$#', UpdatesController::class, 'delete', 'user'],
        ['GET', '#^/groups$#', GroupsController::class, 'index', 'user'],
        ['POST', '#^/groups$#', GroupsController::class, 'create', 'user'],
        ['POST', '#^/groups/(\d+)/(rename|delete)$#', GroupsController::class, 'action', 'user'],
        ['GET', '#^/problems$#', ProblemsController::class, 'index', 'user'],
        ['GET', '#^/notices$#', NoticesController::class, 'index', 'user'],
        ['POST', '#^/notices/read$#', NoticesController::class, 'read', 'user'],
        ['GET', '#^/runs$#', RunsController::class, 'index', 'user'],
        ['GET', '#^/runs/export$#', RunsController::class, 'export', 'user'],
        ['GET', '#^/runs/' . self::ID . '$#', RunsController::class, 'show', 'user'],
        ['GET', '#^/runs/' . self::ID . '/log$#', RunsController::class, 'log', 'user'],
        ['GET', '#^/runs/' . self::ID . '/log/download$#', RunsController::class, 'download', 'user'],
        ['POST', '#^/runs/' . self::ID . '/fetch-log$#', RunsController::class, 'fetchLog', 'user'],
        ['GET', '#^/users$#', UsersController::class, 'index', 'user'],
        ['POST', '#^/users$#', UsersController::class, 'create', 'user'],
        ['POST', '#^/users/(\d+)/(password|disable|enable)$#', UsersController::class, 'action', 'user'],
        ['GET', '#^/audit$#', AuditController::class, 'index', 'user'],
        ['GET', '#^/settings$#', SettingsController::class, 'index', 'user'],
        ['POST', '#^/settings$#', SettingsController::class, 'save', 'user'],
        ['POST', '#^/settings/registration-key$#', SettingsController::class, 'regenerateKey', 'user'],
        ['GET', '#^/settings/notifications$#', NotificationsController::class, 'index', 'user'],
        ['POST', '#^/settings/notifications$#', NotificationsController::class, 'save', 'user'],
        ['GET', '#^/settings/integrations$#', IntegrationsController::class, 'index', 'user'],
        ['POST', '#^/settings/integrations$#', IntegrationsController::class, 'save', 'user'],
        ['POST', '#^/settings/integrations/(zabbix|wall)-token$#', IntegrationsController::class, 'regenerate', 'user'],
        ['GET', '#^/settings/integrations/zabbix-template$#', IntegrationsController::class, 'template', 'user'],
        ['GET', '#^/profile$#', ProfileController::class, 'index', 'user'],
        ['POST', '#^/profile$#', ProfileController::class, 'save', 'user'],
        ['POST', '#^/profile/password$#', ProfileController::class, 'password', 'user'],
        ['GET', '#^/wall$#', WallController::class, 'index', 'token'],
        ['GET', '#^/wall/live$#', WallController::class, 'live', 'token'],
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
        // Pages in the language of the request (the user's choice), the headers set before anything else.
        $response->headers += [
            'Content-Language' => $this->app->lang()->code,
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
        $path = $request->path();
        $route = null;
        $methodMismatch = false;
        foreach (self::ROUTES as [$method, $pattern, $class, $action, $access]) {
            if (preg_match($pattern, $path, $m)) {
                if ($method === $request->method || ($method === 'GET' && $request->method === 'HEAD')) {
                    $route = [$class, $action, $access, array_slice($m, 1)];
                    break;
                }
                $methodMismatch = true;
            }
        }
        $lang = $this->app->lang();
        if ($route !== null && $route[2] === 'token') {
            return $this->tokenRoute($request, ...$route);
        }
        if (!Cidr::allows($request->ip, Cidr::fromStored($this->app->settings()->get('ip_allow_site')))) {
            return Response::text($lang->get('error.ip_not_allowed', ['ip' => $request->ip]), 403);
        }
        if ($route === null) {
            return Response::text($lang->get($methodMismatch ? 'error.method' : 'error.not_found'), $methodMismatch ? 405 : 404);
        }
        [$class, $action, $access, $params] = $route;
        // A page that refreshes its live parts asks for itself again: such a request is treated like a live route.
        if ($access === 'user' && $request->isLive()) {
            $access = 'live';
        }

        $session = Session::start($this->app, $request, $access !== 'live');
        $this->app->useLanguage($session->user()['language'] ?? null);
        $lang = $this->app->lang();
        if ($request->method === 'POST' && !$session->checkCsrf((string) ($request->post['_csrf'] ?? ''))) {
            return Response::text($lang->get('error.csrf'), 400);
        }
        if ($access !== 'public' && $session->user() === null) {
            return $access === 'live'
                ? Response::json(401, ['error' => 'login'])
                : Response::redirect($request->basePath . '/login');
        }

        $view = new View($this->app, $request, $session);
        /** @var Controller $controller */
        $controller = new $class($this->app, $request, $session, $view);
        return $controller->$action(...$params);
    }

    /** The wall screen: the wall token and the integrations allow-list instead of a login, no session cookie. */
    private function tokenRoute(Request $request, string $class, string $action, string $access, array $params): Response
    {
        $lang = $this->app->lang();
        if (!Cidr::allows($request->ip, Cidr::fromStored($this->app->settings()->get('ip_allow_integrations')))) {
            return Response::text($lang->get('error.ip_not_allowed', ['ip' => $request->ip]), 403);
        }
        $token = is_string($request->query['token'] ?? null) ? $request->query['token'] : '';
        if (!$this->app->integrations()->tokenMatches('wall_token', $token)) {
            return Response::text($lang->get('wall.error.token'), 403);
        }
        $view = new View($this->app, $request, null);
        /** @var Controller $controller */
        $controller = new $class($this->app, $request, null, $view);
        return $controller->$action(...$params);
    }
}
