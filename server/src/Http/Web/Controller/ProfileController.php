<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\App;
use Custos\Domain\Users;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/**
 * The signed-in user's own settings (section 7): interface language, theme, colour scheme and password. They live in
 * the menu under the user name on every page (layout.php); each form comes back to the page it was sent from and
 * changes only what it sends.
 */
final class ProfileController extends Controller
{
    /** There is no profile page any more: an old bookmark opens the home page, where the menu is. */
    public function index(): Response
    {
        return $this->redirect('/');
    }

    public function save(): Response
    {
        $me = $this->user();
        $sent = fn(string $key): bool => is_string($this->request->post[$key] ?? null);
        $language = $sent('language') ? $this->post('language') : (string) ($me['language'] ?? '');
        $language = in_array($language, App::LANGUAGES, true) ? $language : null;
        $theme = $sent('theme') ? $this->post('theme') : (string) ($me['theme'] ?? '');
        $theme = in_array($theme, Users::THEMES, true) ? $theme : 'auto';
        $color = $sent('color') ? $this->post('color') : (string) ($me['color'] ?? '');
        $color = in_array($color, Users::COLORS, true) ? $color : Users::COLORS[0];
        $this->app->users()->setPreferences((int) $me['id'], $language, $theme, $color);
        $this->audit('profile.update', 'user', (string) $me['id'], ['language' => $language ?? 'default', 'theme' => $theme, 'color' => $color]);
        // The page itself shows the new theme or language: no message needed.
        return $this->back();
    }

    public function password(): Response
    {
        $me = $this->user();
        $users = $this->app->users();
        if ($users->verify((string) $me['login'], $this->post('current')) === null) {
            $this->session->flash('error', $this->t('profile.error.current'));
            return $this->back();
        }
        if ($this->post('password') !== $this->post('repeat')) {
            $this->session->flash('error', $this->t('profile.error.repeat'));
            return $this->back();
        }
        try {
            $users->setPassword((int) $me['id'], $this->post('password'));
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), ['min' => Users::MIN_PASSWORD]));
            return $this->back();
        }
        $this->audit('profile.password', 'user', (string) $me['id']);
        $this->session->flash('info', $this->t('profile.flash.password'));
        return $this->back();
    }

    /** Back to the page the menu was used on: a path of this site only, never another host. */
    private function back(): Response
    {
        $back = $this->post('back');
        $local = str_starts_with($back, '/') && !str_starts_with($back, '//') && !str_contains($back, '\\')
            && preg_match('/[\x00-\x1f\x7f]/', $back) === 0;
        if (!$local) {
            return $this->redirect('/');
        }
        return Response::redirect($this->request->basePath . $back);
    }
}
