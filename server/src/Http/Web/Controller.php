<?php

declare(strict_types=1);

namespace Custos\Http\Web;

use Custos\App;
use Custos\Http\Request;
use Custos\Http\Response;

abstract class Controller
{
    public function __construct(
        protected readonly App $app,
        protected readonly Request $request,
        protected readonly Session $session,
        protected readonly View $view
    ) {
    }

    protected function render(string $template, array $vars, string $title, int $status = 200): Response
    {
        return $this->view->render($template, $vars, $title, $status);
    }

    protected function redirect(string $path, array $query = []): Response
    {
        return Response::redirect($this->view->url($path, $query));
    }

    protected function notFound(): Response
    {
        return Response::text($this->t('error.not_found'), 404);
    }

    protected function t(string $key, array $params = []): string
    {
        return $this->app->lang()->get($key, $params);
    }

    protected function user(): array
    {
        return $this->session->user() ?? throw new \LogicException('No signed-in user.');
    }

    protected function post(string $key): string
    {
        $value = $this->request->post[$key] ?? '';
        return is_string($value) ? $value : '';
    }

    protected function query(string $key): string
    {
        $value = $this->request->query[$key] ?? '';
        return is_string($value) ? trim($value) : '';
    }

    protected function audit(string $action, ?string $objectType = null, ?string $objectId = null, array $details = []): void
    {
        $this->app->audit()->log($this->session->user(), $this->request->ip, $action, $objectType, $objectId, $details);
    }

    /** Converts a date typed in a filter ("2026-10-12", site time zone) to UTC for queries. */
    protected function dateToUtc(string $date, bool $endOfDay = false): ?string
    {
        if (!preg_match('/^\d{4}-\d{2}-\d{2}$/', $date)) {
            return null;
        }
        try {
            $local = new \DateTimeImmutable($date, new \DateTimeZone($this->app->config->timezone()));
        } catch (\Exception) {
            return null;
        }
        if ($endOfDay) {
            $local = $local->modify('+1 day');
        }
        return $local->setTimezone(new \DateTimeZone('UTC'))->format('Y-m-d H:i:s');
    }

    protected function page(): int
    {
        return max(1, (int) $this->query('page'));
    }
}
