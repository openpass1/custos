<?php

declare(strict_types=1);

namespace Custos\Http\Web;

use Custos\App;
use Custos\Http\Request;
use Custos\Http\Response;

/**
 * Renders PHP templates from templates/ inside templates/layout.php.
 * The helpers in src/helpers.php (e, t, url, fmt_*) work through View::$current.
 * A page whose blocks show what agents and cron change passes 'live' => seconds: app.js then asks for the page again
 * that often and puts the blocks marked data-part="name" in place without reloading it.
 */
final class View
{
    public static ?View $current = null;

    public function __construct(
        public readonly App $app,
        public readonly Request $request,
        public readonly ?Session $session
    ) {
    }

    public function render(string $template, array $vars, string $title, int $status = 200): Response
    {
        self::$current = $this;
        $content = $this->capture($template, $vars);
        $html = $this->capture('layout', [
            'title' => $title,
            'content' => $content,
            'user' => $this->session?->user(),
            // A message after an action belongs to the page the person opens, not to a refresh of an open page.
            'flash' => $this->request->isLive() ? [] : ($this->session?->takeFlash() ?? []),
            'section' => $vars['section'] ?? '',
            'scripts' => $vars['scripts'] ?? [],
            'live' => (int) ($vars['live'] ?? 0),
        ]);
        return Response::html($html, $status);
    }

    /** A whole page without the layout (the wall screen has its own). */
    public function page(string $template, array $vars, int $status = 200): Response
    {
        self::$current = $this;
        return Response::html($this->capture($template, $vars), $status);
    }

    /** Light, dark or auto (follows the system) for the page: the user's choice. */
    public function theme(): string
    {
        $theme = (string) ($this->session?->user()['theme'] ?? 'auto');
        return in_array($theme, ['light', 'dark'], true) ? $theme : 'auto';
    }

    /** The colour scheme of the page (app.css, "Colour schemes"): the user's choice, else the base one. */
    public function color(): string
    {
        $color = (string) ($this->session?->user()['color'] ?? '');
        return in_array($color, \Custos\Domain\Users::COLORS, true) ? $color : \Custos\Domain\Users::COLORS[0];
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
