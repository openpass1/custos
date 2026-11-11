<?php

declare(strict_types=1);

namespace Custos\Http\Web\Controller;

use Custos\App;
use Custos\Domain\Users;
use Custos\Http\Response;
use Custos\Http\Web\Controller;

/** The signed-in user's own page (section 7): interface language, theme and password. */
final class ProfileController extends Controller
{
    public function index(): Response
    {
        return $this->render('profile/index', [
            'section' => 'profile',
            'me' => $this->user(),
            'languages' => $this->app->languages(),
            'defaultLanguage' => $this->app->defaultLanguage(),
        ], $this->t('profile.title'));
    }

    public function save(): Response
    {
        $me = $this->user();
        $language = $this->post('language');
        $language = in_array($language, App::LANGUAGES, true) ? $language : null;
        $theme = in_array($this->post('theme'), Users::THEMES, true) ? $this->post('theme') : 'auto';
        $this->app->users()->setPreferences((int) $me['id'], $language, $theme);
        $this->audit('profile.update', 'user', (string) $me['id'], ['language' => $language ?? 'default', 'theme' => $theme]);
        // The message is already in the new language.
        $this->app->useLanguage($language);
        $this->session->flash('info', $this->t('profile.flash.saved'));
        return $this->redirect('/profile');
    }

    public function password(): Response
    {
        $me = $this->user();
        $users = $this->app->users();
        if ($users->verify((string) $me['login'], $this->post('current')) === null) {
            $this->session->flash('error', $this->t('profile.error.current'));
            return $this->redirect('/profile');
        }
        if ($this->post('password') !== $this->post('repeat')) {
            $this->session->flash('error', $this->t('profile.error.repeat'));
            return $this->redirect('/profile');
        }
        try {
            $users->setPassword((int) $me['id'], $this->post('password'));
        } catch (\InvalidArgumentException $e) {
            $this->session->flash('error', $this->t($e->getMessage(), ['min' => Users::MIN_PASSWORD]));
            return $this->redirect('/profile');
        }
        $this->audit('profile.password', 'user', (string) $me['id']);
        $this->session->flash('info', $this->t('profile.flash.password'));
        return $this->redirect('/profile');
    }
}
