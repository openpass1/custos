<?php

declare(strict_types=1);

namespace Custos\Http\Web;

use Custos\App;
use Custos\Http\Request;
use Custos\Http\Response;

/**
 * Renders PHP templates from templates/ inside templates/layout.php.
 * The helpers in src/helpers.php (e, t, url, fmt_*) work through View::$current.
 */
final class View
{
    public static ?View $current = null;

    public function __construct(
        public readonly App $app,
        public readonly Request $request,
        public readonly Session $session
    ) {
    }

    public function render(string $template, array $vars, string $title, int $status = 200): Response
    {
        self::$current = $this;
        $content = $this->capture($template, $vars);
        $html = $this->capture('layout', [
            'title' => $title,
            'content' => $content,
            'user' => $this->session->user(),
            'flash' => $this->session->takeFlash(),
            'section' => $vars['section'] ?? '',
        ]);
        return Response::html($html, $status);
    }

    /** A template without the layout, for blocks that the page refreshes by itself. */
    public function partial(string $template, array $vars): string
    {
        self::$current = $this;
        return $this->capture($template, $vars);
    }

    public function url(string $path, array $query = []): string
    {
        $query = array_filter($query, fn($v) => $v !== null && $v !== '');
        return $this->request->basePath . $path . ($query === [] ? '' : '?' . http_build_query($query));
    }

    /** URL of a file in public/assets with its modification time, so browsers reload it after an update. */
    public function asset(string $file): string
    {
        $mtime = @filemtime($this->app->root() . '/public/assets/' . $file) ?: 0;
        return $this->request->basePath . '/assets/' . $file . '?v=' . $mtime;
    }

    private function capture(string $template, array $vars): string
    {
        $file = $this->app->root() . '/templates/' . $template . '.php';
        extract($vars, EXTR_SKIP);
        ob_start();
        try {
            include $file;
        } catch (\Throwable $e) {
            ob_end_clean();
            throw $e;
        }
        return (string) ob_get_clean();
    }
}
